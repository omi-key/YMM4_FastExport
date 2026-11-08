using System.IO;
using FastPsdTachiePlugin;

// Tests of the parsed-document sharing logic (SharedDocStore). No PSD: dummies count loads and disposals.
internal static class DocStoreTests
{
    private static int s_fail;
    private static void Check(bool ok, string name)
    {
        if (!ok) { s_fail++; Console.WriteLine("FAIL: " + name); }
    }

    private sealed class Doc
    {
        public readonly string Name; public int Users; public bool Disposed;
        public Doc(string n) { Name = n; }
    }

    private static SharedDocStore<Doc>.DocKey K(string path, long stamp = 1, long len = 100) => new(path, stamp, len);

    public static int Run()
    {
        s_fail = 0;
        long disposed = 0;

        // 1) One load per key; paths are case-insensitive; a document with no users stays idle and can be reused
        {
            var s = new SharedDocStore<Doc>(d => { d.Disposed = true; Interlocked.Increment(ref disposed); }, 1000, TimeSpan.FromSeconds(600));
            int loads = 0;
            Func<(Doc, long)?> load = () => { loads++; return (new Doc("a"), 100); };
            var l1 = s.Acquire(K("C:\\x\\a.psd"), load);
            var l2 = s.Acquire(K("c:\\X\\A.PSD"), load);
            Check(l1 is not null && l2 is not null && ReferenceEquals(l1.Value, l2.Value) && loads == 1, "doc: same key loads once (case-insensitive path)");
            s.Release(l1); s.Release(l1);   // a double Release is ignored
            Check(!l1!.Value.Disposed && s.Snapshot().Idle == 0, "doc: still referenced by l2 (double release ignored)");
            s.Release(l2);
            Check(s.Snapshot() == (1, 1, 100) && !l1.Value.Disposed, "doc: idle after last release, not disposed");
            var d1 = l1.Value;
            var l3 = s.Acquire(K("C:\\x\\a.psd"), load);
            Check(loads == 1 && ReferenceEquals(l3!.Value, d1) && s.Snapshot().Idle == 0, "doc: reuse from idle");
            s.Release(l3);
        }

        // 2) A different version (mtime/size) is a separate load; the old one is disposed when its last user is gone (valid until then)
        {
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 100000, TimeSpan.FromSeconds(600));
            var a1 = s.Acquire(K("p", 1), () => (new Doc("v1"), 10));
            var a2 = s.Acquire(K("p", 2), () => (new Doc("v2"), 10));
            Check(a1 is not null && a2 is not null && !ReferenceEquals(a1.Value, a2.Value), "doc: new stamp = new document");
            var dv1 = a1!.Value; var dv2 = a2!.Value;
            Check(!dv1.Disposed, "doc: stale version alive while in use");
            s.Release(a1);
            Check(dv1.Disposed && s.Snapshot().Entries == 1, "doc: stale version disposed after last release");
            var a3 = s.Acquire(K("p", 3, 200), () => (new Doc("v3"), 10));   // a different size is a different version too
            var dv3 = a3!.Value;
            Check(dv2.Name == "v2" && !ReferenceEquals(dv3, dv2), "doc: new length = new document");
            s.Release(a2); s.Release(a3);
            Check(dv2.Disposed && !dv3.Disposed, "doc: only newest version retained idle");
            var other = s.Acquire(K("q", 1), () => (new Doc("q"), 10));   // other paths are unaffected
            Check(!dv3.Disposed, "doc: other path does not supersede");
            s.Release(other);
        }

        // 3) When the idle total (bytes) exceeds the limit, dispose oldest first; documents in use are kept
        {
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 250, TimeSpan.FromSeconds(600));
            var ls = Enumerable.Range(0, 4).Select(i => s.Acquire(K("f" + i), () => (new Doc("f" + i), 100))!).ToList();
            var ds = ls.Select(l => l.Value).ToList();
            s.Release(ls[0]); s.Release(ls[1]);
            Check(!ds[0].Disposed && !ds[1].Disposed, "doc: idle within budget");
            s.Release(ls[2]);   // idle 300 > 250: the oldest, f0, is disposed
            Check(ds[0].Disposed && !ds[1].Disposed && !ds[2].Disposed && !ds[3].Disposed, "doc: oldest idle evicted, in-use kept");
            s.Release(ls[3]);
            Check(ds[1].Disposed && !ds[2].Disposed, "doc: next oldest evicted");
            s.PurgeIdle();
            Check(ds.All(d => d.Disposed) && s.Snapshot() == (0, 0, 0), "doc: purge idle");
        }

        // 4) Idle TTL
        {
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 100000, TimeSpan.FromMilliseconds(50));
            var l = s.Acquire(K("t"), () => (new Doc("t"), 10))!;
            var dt = l.Value;
            s.Release(l);
            Thread.Sleep(80);
            s.Sweep();
            Check(dt.Disposed && s.Snapshot() == (0, 0, 0), "doc: idle ttl sweeps");
            var u = s.Acquire(K("t2"), () => (new Doc("t2"), 10))!;
            Thread.Sleep(80); s.Sweep();
            Check(!u.Value.Disposed, "doc: ttl never touches in-use");
            s.Release(u);
        }

        // 5) A failed load (null / exception) returns null, is not remembered, and is retried on the next request
        {
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 1000, TimeSpan.FromSeconds(600));
            Check(s.Acquire(K("bad"), () => null) is null, "doc: null load -> null");
            Check(s.Acquire(K("bad"), () => throw new IOException("x")) is null, "doc: throwing load -> null");
            Check(s.Snapshot().Entries == 0 && s.LoadFailures == 2, "doc: failures not retained");
            var ok = s.Acquire(K("bad"), () => (new Doc("ok"), 10));
            Check(ok is not null && ok.Value.Name == "ok", "doc: retry after failure succeeds");
            s.Release(ok);
        }

        // 6) Concurrent requests for one key load once; waiters get the same value
        {
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 100000, TimeSpan.FromSeconds(600));
            int loads = 0;
            var results = new SharedDocStore<Doc>.Lease?[16];
            var go = new ManualResetEventSlim();
            var ts = Enumerable.Range(0, 16).Select(i => new Thread(() =>
            {
                go.Wait();
                results[i] = s.Acquire(K("same"), () => { Interlocked.Increment(ref loads); Thread.Sleep(50); return (new Doc("same"), 10); });
            })).ToList();
            ts.ForEach(t => t.Start()); go.Set(); ts.ForEach(t => t.Join());
            Check(loads == 1 && results.All(r => r is not null && ReferenceEquals(r.Value, results[0]!.Value)), "doc: concurrent same-key loads once");
            foreach (var r in results) s.Release(r);
        }

        // 7) Concurrent stress: a document in use is never disposed, re-acquiring after release works, the limit holds
        {
            long bad = 0;
            var s = new SharedDocStore<Doc>(d => d.Disposed = true, 300, TimeSpan.FromMilliseconds(5));
            var ts = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                var rng = new Random(t * 7 + 1);
                for (int i = 0; i < 20000; i++)
                {
                    int p = rng.Next(6); long stamp = 1 + rng.Next(3);
                    var l = s.Acquire(K("p" + p, stamp), () => (new Doc($"p{p}@{stamp}"), 100));
                    if (l is null) { Interlocked.Increment(ref bad); continue; }
                    var d = l.Value;
                    Interlocked.Increment(ref d.Users);
                    if (d.Disposed || d.Name != $"p{p}@{stamp}") Interlocked.Increment(ref bad);
                    if ((i & 15) == 0) Thread.Yield();
                    if (d.Disposed) Interlocked.Increment(ref bad);
                    Interlocked.Decrement(ref d.Users);
                    s.Release(l);
                    if ((i & 255) == 0) s.Sweep();
                }
            })).ToList();
            ts.ForEach(x => x.Start()); ts.ForEach(x => x.Join());
            var sn = s.Snapshot();
            Check(bad == 0, "doc: stress - no in-use disposal / wrong doc / null (" + bad + ")");
            Check(sn.IdleBytes <= 300 && sn.Entries == sn.Idle, "doc: stress - idle within budget, nothing leaked in use");
            s.PurgeIdle();
            Check(s.Snapshot() == (0, 0, 0), "doc: stress - drained");
        }

        Console.WriteLine(s_fail == 0 ? "doc-store tests: all passed" : $"doc-store tests: {s_fail} FAILED");
        return s_fail;
    }
}
