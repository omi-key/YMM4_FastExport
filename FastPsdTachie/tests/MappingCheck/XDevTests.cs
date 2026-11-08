using FastPsdTachiePlugin;

// Tests of cross-device sharing logic (CrossDeviceStore, device-free keys, compose once + copy). No D2D.
internal static class XDevTests
{
    private static int s_fail;
    private static void Check(bool ok, string name)
    {
        if (!ok) { s_fail++; Console.WriteLine("FAIL: " + name); }
    }

    private static PlanIdentity Plan(int device, string path = "C:\\a.psd", long stamp = 1, ulong fp = 7)
        => new(device, path, stamp, 100, 10, 10, fp, fp + 1);

    private static StateKey Key(PlanIdentity p, params string?[] picks) => new(p, picks);

    public static int Run()
    {
        s_fail = 0;

        // 1) Device-free key: equal across devices, different if anything else differs
        {
            var a = Key(Plan(1).DeviceFree, "m1", "e0");
            var b = Key(Plan(2).DeviceFree, "m1", "e0");
            Check(a.Equals(b) && a.GetHashCode() == b.GetHashCode(), "xkey: same across devices");
            Check(!a.Equals(Key(Plan(1, path: "C:\\b.psd").DeviceFree, "m1", "e0")), "xkey: path differs");
            Check(!a.Equals(Key(Plan(1, stamp: 2).DeviceFree, "m1", "e0")), "xkey: stamp differs");
            Check(!a.Equals(Key(Plan(1, fp: 8).DeviceFree, "m1", "e0")), "xkey: fingerprint differs");
            Check(!a.Equals(Key(Plan(1).DeviceFree, "m2", "e0")), "xkey: picks differ");
            Check(!Key(Plan(1), "m1", "e0").Equals(Key(Plan(2), "m1", "e0")), "xkey: ordinary key still separates devices");
            var p = Plan(3);
            Check(ReferenceEquals(p.DeviceFree, p.DeviceFree) && p.DeviceFree.DeviceId == 0, "xkey: DeviceFree cached, DeviceId 0");
        }

        // 2) Store: put/get, first writer wins, LRU eviction at the byte limit, oversized entry rejected, Clear
        {
            var s = new CrossDeviceStore<object>(100);
            var p = Plan(0);
            object o1 = new(), o1b = new(), o2 = new(), o3 = new();
            Check(s.TryGet(Key(p, "a")) is null, "store: miss");
            Check(s.Put(Key(p, "a"), o1, 40) && s.Put(Key(p, "b"), o2, 40), "store: put");
            Check(s.Put(Key(p, "a"), o1b, 40) && ReferenceEquals(s.TryGet(Key(p, "a")), o1), "store: first writer wins");
            s.TryGet(Key(p, "a"));                       // make a most recently used
            Check(s.Put(Key(p, "c"), o3, 40), "store: put c (evicts)");
            Check(s.TryGet(Key(p, "b")) is null && s.TryGet(Key(p, "a")) is not null && s.TryGet(Key(p, "c")) is not null, "store: LRU victim is b");
            Check(s.Snapshot().Bytes == 80 && s.Evictions == 1, "store: bytes/evictions");
            Check(!s.Put(Key(p, "big"), new object(), 101) && s.Rejected == 1 && s.TryGet(Key(p, "big")) is null, "store: oversized rejected");
            var held = s.TryGet(Key(p, "a"));
            s.Clear();
            Check(s.Snapshot() == (0, 0) && held is not null && s.TryGet(Key(p, "a")) is null, "store: clear (held reference stays valid)");
        }

        // 3) Concurrent stress: stays within the limit and consistent
        {
            var s = new CrossDeviceStore<object>(1000);
            var p = Plan(0);
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                var rng = new Random(t);
                for (int i = 0; i < 20000; i++)
                {
                    var k = Key(p, "k" + rng.Next(60));
                    if (s.TryGet(k) is null) s.Put(k, new object(), 10 + rng.Next(40));
                }
            })).ToList();
            threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
            var snap = s.Snapshot();
            Check(snap.Bytes <= 1000 && snap.Bytes >= 0 && snap.Entries > 0, "store stress: bytes within limit (" + snap.Bytes + ")");
        }

        // 4) Cooperation: P "devices" request the same states -> each state is composed once, the rest are copies.
        //    Composition is serialized globally (SharedBitmapCache's compose lock), so later requesters find it in the store.
        {
            const int P = 6, KEYS = 12;
            var cache = new SharedBitmapCache<FakeBitmap>(1000, 1L << 40, b => b.Disposed = true, 20000);
            for (int d = 1; d <= P; d++) cache.Register(d);
            Check(cache.DeviceCount == P, "device count after register (" + cache.DeviceCount + ")");
            var store = new CrossDeviceStore<object>(1L << 40);
            int composes = 0, uploads = 0, id = 0;
            var results = new FakeBitmap?[P, KEYS];
            var threads = Enumerable.Range(1, P).Select(d => new Thread(() =>
            {
                for (int i = 0; i < KEYS; i++)
                {
                    int k = (i + d * 2) % KEYS;   // different order per device
                    var key = Key(Plan(d), "s" + k);
                    var xkey = Key(Plan(d).DeviceFree, "s" + k);
                    var e = cache.TryAcquire(key) ?? cache.Acquire(key, () =>
                    {
                        if (store.TryGet(xkey) is { } blk) { Interlocked.Increment(ref uploads); return (new FakeBitmap { Id = (int)(long)blk }, 10); }
                        Interlocked.Increment(ref composes);
                        Thread.Sleep(5);
                        var f = new FakeBitmap { Id = Interlocked.Increment(ref id) };
                        store.Put(xkey, (object)(long)f.Id, 10);
                        return (f, 10);
                    }, out _);
                    results[d - 1, k] = e?.Value;
                    cache.Release(e);
                }
            })).ToList();
            threads.ForEach(t => t.Start()); threads.ForEach(t => t.Join());
            Check(composes == KEYS, "xdev: composes == keys (got " + composes + ")");
            Check(uploads == KEYS * (P - 1), "xdev: uploads == keys*(P-1) (got " + uploads + ")");
            bool same = true;
            for (int k = 0; k < KEYS; k++) for (int d = 1; d < P; d++) same &= results[d, k]?.Id == results[0, k]?.Id && results[0, k] is not null;
            Check(same, "xdev: every device got the same content for each state");
            for (int d = 1; d <= P; d++) cache.Unregister(d);
            Check(cache.DeviceCount == 0, "device count after unregister");
        }

        Console.WriteLine(s_fail == 0 ? "xdev tests: all passed" : "xdev tests: FAILURES=" + s_fail);
        return s_fail;
    }
}
