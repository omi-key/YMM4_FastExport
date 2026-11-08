using FastPsdTachiePlugin;

// Tests of the shared cache logic (no D2D; fake objects stand in for bitmaps).
internal sealed class FakeBitmap
{
    public int Id; public volatile bool Disposed;
}

internal static class CacheTests
{
    private static int s_fail;
    private static void Check(bool ok, string name)
    {
        if (!ok) { s_fail++; Console.WriteLine("FAIL: " + name); }
    }

    private static PlanIdentity Plan(int device, string path = "C:\\a.psd", long stamp = 1, ulong fp = 7)
        => new(device, path, stamp, 100, 10, 10, fp, fp + 1);

    private static StateKey Key(PlanIdentity p, params string?[] picks) => new(p, picks);

    private static SharedBitmapCache<FakeBitmap> New(int count, long bytes = 1L << 40, int waitMs = 5000)
        => new(count, bytes, b => b.Disposed = true, waitMs);

    private static int s_id;
    private static (FakeBitmap?, long) Make() => (new FakeBitmap { Id = Interlocked.Increment(ref s_id) }, 10);

    public static int Run()
    {
        s_fail = 0;

        // 1) Concurrent access to one key: composed once, everyone gets the same entry
        {
            var c = New(10);
            c.Register(1);
            var k = Key(Plan(1), "m1", "e0");
            int composes = 0;
            const int N = 16;
            var results = new CacheEntry<FakeBitmap>?[N];
            using var start = new ManualResetEventSlim(false);
            var threads = Enumerable.Range(0, N).Select(i => new Thread(() =>
            {
                start.Wait();
                var e = c.TryAcquire(k) ?? c.Acquire(k, () => { Interlocked.Increment(ref composes); Thread.Sleep(100); return Make(); }, out _);
                results[i] = e;
            })).ToList();
            threads.ForEach(t => t.Start());
            start.Set();
            threads.ForEach(t => t.Join());
            Check(composes == 1, "same-key concurrent: composed once (got " + composes + ")");
            Check(results.All(r => r is not null && ReferenceEquals(r, results[0])), "same-key concurrent: same entry for all");
            Check(results[0]!.Pins == N, "same-key concurrent: pins == threads (got " + results[0]!.Pins + ")");
            foreach (var r in results) c.Release(r);
            Check(results[0]!.Pins == 0, "same-key concurrent: pins released");
        }

        // 2) Different devices never share
        {
            var c = New(10);
            c.Register(1); c.Register(2);
            int composes = 0;
            var e1 = c.Acquire(Key(Plan(1), "x"), () => { composes++; return Make(); }, out _);
            var e2 = c.Acquire(Key(Plan(2), "x"), () => { composes++; return Make(); }, out _);
            Check(composes == 2 && !ReferenceEquals(e1, e2) && !ReferenceEquals(e1!.Value, e2!.Value), "device separation");
            Check(c.TryAcquire(Key(Plan(1), "x")) is not null, "device 1 still hits");
            // Same device and same content (separate identity instance) shares
            var e3 = c.TryAcquire(Key(Plan(1, "C:\\A.PSD"), "x"));
            Check(e3 is not null && ReferenceEquals(e3, e1), "equal identity (case-insensitive path) shares entry");
            // When device 1's last source goes away, only device 1's images are purged
            c.Release(e1); c.Release(e1); c.Release(e3);   // one pin each from Acquire, TryAcquire and e3
            c.Unregister(1);
            Check(c.TryAcquire(Key(Plan(1), "x")) is null, "device 1 purged after its last source");
            Check(c.TryAcquire(Key(Plan(2), "x")) is not null, "device 2 untouched");
        }

        // 2b) Images survive the grace period after a device's last source goes; a new source on the same device within it keeps them; they are dropped after it
        {
            var c = new SharedBitmapCache<FakeBitmap>(100, 1L << 40, b => b.Disposed = true, 5000, deviceGraceSec: 1.5);
            c.Register(1);
            var e = c.Acquire(Key(Plan(1), "g"), Make, out _)!;
            c.Release(e);
            c.Unregister(1);
            var hit = c.TryAcquire(Key(Plan(1), "g"));
            Check(hit is not null && !hit.Value.Disposed, "grace: image kept right after the last source (no purge yet)");
            c.Release(hit);
            c.Register(1);
            Thread.Sleep(2200);
            var hit2 = c.TryAcquire(Key(Plan(1), "g"));
            Check(hit2 is not null, "grace: a new source registered within the grace keeps the image beyond the grace time");
            c.Release(hit2);
            c.Unregister(1);
            Thread.Sleep(3200);
            Check(c.TryAcquire(Key(Plan(1), "g")) is null, "grace: image purged after the grace time without a new source");
        }

        // 3) Eviction safety: entries in use are never evicted; evicted images are disposed only after grace ticks
        {
            var c = New(3);
            c.Register(1);
            var pinned = c.Acquire(Key(Plan(1), "p"), Make, out _)!;   // stays pinned
            var others = new List<CacheEntry<FakeBitmap>>();
            for (int i = 0; i < 6; i++)
            {
                var e = c.Acquire(Key(Plan(1), "o" + i), Make, out _)!;
                others.Add(e);
                c.Release(e);   // release at once
            }
            var (entries, _, _) = c.Snapshot();
            Check(entries <= 3, "count limit enforced (entries=" + entries + ")");
            Check(!pinned.Value.Disposed && c.TryAcquire(Key(Plan(1), "p")) is not null, "pinned entry never evicted");
            c.Release(pinned);
            Check(c.Stats.Evictions >= 4, "evictions happened");
            var evictedNotYet = others.Where(o => o.Detached || o.Node is null).ToList();
            Check(evictedNotYet.Count > 0 && evictedNotYet.All(o => !o.Value.Disposed), "evicted bitmaps not disposed immediately (grace)");
            for (int i = 0; i < 20; i++) c.Tick();
            Check(evictedNotYet.All(o => o.Value.Disposed), "evicted bitmaps disposed after grace ticks");
            Check(others.Where(o => o.Node is not null).All(o => !o.Value.Disposed), "live entries not disposed");
        }

        // 4) Process-wide byte limit (held across several PSDs)
        {
            var c = new SharedBitmapCache<FakeBitmap>(100, 35, b => b.Disposed = true);
            c.Register(1);
            for (int i = 0; i < 10; i++)
            {
                var e = c.Acquire(Key(Plan(1, "C:\\f" + (i % 3) + ".psd", fp: (ulong)i), "k"), Make, out _);
                c.Release(e);
            }
            Check(c.Snapshot().Bytes <= 35, "process-wide byte limit (bytes=" + c.Snapshot().Bytes + ")");
        }

        // 5) A PSD update (different mtime/size) drops the old version; if still in use, after Release
        {
            var c = New(10);
            c.Register(1);
            var old = c.Acquire(Key(Plan(1, stamp: 1), "a"), Make, out _)!;
            var neu = c.Acquire(Key(Plan(1, stamp: 2), "a"), Make, out _)!;
            Check(c.TryAcquire(Key(Plan(1, stamp: 1), "a")) is null, "stale version purged from map");
            Check(!old.Value.Disposed, "stale but pinned stays alive");
            c.Release(old);
            for (int i = 0; i < 20; i++) c.Tick();
            Check(old.Value.Disposed, "stale disposed after release + grace");
            Check(!neu.Value.Disposed, "new version alive");
        }

        // 6) Compose failure/exception: waiters get null, no pins leak, retry works
        {
            var c = New(10);
            c.Register(1);
            var k = Key(Plan(1), "f");
            var r = c.Acquire(k, () => (null, 0), out bool here);
            Check(r is null && !here, "null compose -> null");
            bool threw = false;
            try { c.Acquire(k, () => throw new InvalidOperationException("boom"), out _); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "exception propagates to composing thread");
            var ok = c.Acquire(k, Make, out _);
            Check(ok is not null && ok.Pins == 1, "retry after failure works");

            // Compose fails while someone is waiting
            var k2 = Key(Plan(1), "g");
            CacheEntry<FakeBitmap>? waiterResult = new CacheEntry<FakeBitmap>();
            var gate = new ManualResetEventSlim(false);
            var owner = new Thread(() => { try { c.Acquire(k2, () => { gate.Wait(); Thread.Sleep(50); return (null, 0); }, out _); } catch { } });
            owner.Start();
            Thread.Sleep(100);
            var waiter = new Thread(() => { waiterResult = c.Acquire(k2, Make, out _); });
            waiter.Start();
            Thread.Sleep(100);
            gate.Set();
            owner.Join(); waiter.Join();
            Check(waiterResult is null, "waiter gets null when owner's compose fails");
        }

        // 7) A wait timeout revokes the pin (no pin leak)
        {
            var c = New(10, waitMs: 100);
            c.Register(1);
            var k = Key(Plan(1), "t");
            var gate = new ManualResetEventSlim(false);
            CacheEntry<FakeBitmap>? ownerEntry = null;
            var owner = new Thread(() => ownerEntry = c.Acquire(k, () => { gate.Wait(); return Make(); }, out _));
            owner.Start();
            Thread.Sleep(50);
            var w = c.Acquire(k, Make, out _);
            Check(w is null && c.Stats.WaitTimeouts == 1, "waiter times out");
            gate.Set(); owner.Join();
            Check(ownerEntry is not null && ownerEntry.Pins == 1, "no leaked pins after waiter timeout (pins=" + ownerEntry?.Pins + ")");
        }

        // 8) When the last source goes: everything disposed without grace, BecameIdle raised
        {
            var c = New(10);
            bool idle = false;
            c.BecameIdle += () => idle = true;
            c.Register(1);
            var e = c.Acquire(Key(Plan(1), "z"), Make, out _)!;
            c.Release(e);
            c.Unregister(1);
            Check(idle && e.Value.Disposed, "idle: all disposed + notified");
        }

        // 9) Content hash: order-independent, content-dependent
        {
            var a = new HashSet<string> { "i1", "i2", "i3" };
            var b = new HashSet<string> { "i3", "i1", "i2" };
            var r = new HashSet<string> { "i9" };
            Check(AnimationPlan.Fingerprint(a, r) == AnimationPlan.Fingerprint(b, r), "fingerprint order independent");
            Check(AnimationPlan.Fingerprint(a, r) != AnimationPlan.Fingerprint(new HashSet<string> { "i1", "i2", "i4" }, r), "fingerprint content dependent");
            Check(AnimationPlan.Fingerprint(a, r) != AnimationPlan.Fingerprint(a, new HashSet<string> { "i8" }), "fingerprint removed-set dependent");
        }

        // 10) Default limit: 20% of the VRAM budget clamped to [256MB, 3GB]; 1GB when unavailable
        {
            const long MB = 1024L * 1024;
            Check(CachePolicy.DefaultBytes(null) == 1024 * MB, "policy: unavailable -> 1GB");
            Check(CachePolicy.DefaultBytes(0) == 1024 * MB, "policy: zero budget -> 1GB");
            Check(CachePolicy.DefaultBytes(14000UL * (ulong)MB) == (long)(14000 * MB * 0.2), "policy: 14GB budget -> 20%");
            Check(CachePolicy.DefaultBytes(40000UL * (ulong)MB) == 3L * 1024 * MB, "policy: clamp max 3GB");
            Check(CachePolicy.DefaultBytes(512UL * (ulong)MB) == 256 * MB, "policy: clamp min 256MB");
        }

        // 11) Shrink: halves the limit and evicts by LRU; entries in use stay; never below the floor
        {
            var c = new SharedBitmapCache<FakeBitmap>(100, 100, b => b.Disposed = true, minBytes: 20);
            c.Register(1);
            var es = new List<CacheEntry<FakeBitmap>>();
            for (int i = 0; i < 10; i++) es.Add(c.Acquire(Key(Plan(1), "s" + i), Make, out _)!);   // 10 bytes each, all pinned
            for (int i = 1; i < 10; i++) c.Release(es[i]);                                         // only es[0] stays in use
            Check(c.Snapshot().Bytes == 100, "shrink: initial bytes");
            Check(c.Shrink(), "shrink: shrinks");
            Check(c.MaxBytes == 50 && c.Snapshot().Bytes <= 50, "shrink: limit halved and evicted (bytes=" + c.Snapshot().Bytes + ")");
            Check(c.TryAcquire(Key(Plan(1), "s0")) is not null, "shrink: pinned (oldest) entry survives");
            Check(c.Stats.Shrinks == 1, "shrink: counted");
            c.Shrink(); c.Shrink(); c.Shrink();
            Check(c.MaxBytes == 20, "shrink: floor respected (limit=" + c.MaxBytes + ")");
            Check(!c.Shrink(), "shrink: no-op at floor");
        }

        Console.WriteLine(s_fail == 0 ? "cache tests: all passed" : "cache tests: FAILURES=" + s_fail);
        return s_fail;
    }
}
