using System.Diagnostics;
using System.Runtime.CompilerServices;
using Vortice.Direct2D1;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace FastPsdTachiePlugin;

/// <summary>Per-render-device ID (keyed on the managed ID2D1Device object). Bitmaps belong to a device, so different devices never share them. IDs are never reused.</summary>
internal static class DeviceTokens
{
    private sealed class Token { public int Id; }
    private static readonly ConditionalWeakTable<object, Token> Table = new();
    private static int s_next;

    public static int IdOf(IGraphicsDevicesAndContext devices)
    {
        object? key = null;
        try { key = devices.D2D?.Device; } catch { }
        // Device unidentifiable: use a per-context ID, i.e. share with no other source.
        key ??= devices;
        return Table.GetValue(key, _ => new Token { Id = Interlocked.Increment(ref s_next) }).Id;
    }
}

/// <summary>The single process-wide bitmap cache. Its limit comes from the VRAM budget of the first source's device.</summary>
internal static class SharedCaches
{
    private static readonly object Gate = new();
    private static SharedBitmapCache<ID2D1Bitmap>? s_cache;
    private static long s_lastShrinkTicks;

    // How long a device's images are kept after its last source is disposed (see SharedBitmapCache.deviceGraceSec).
    private const double DeviceGraceSeconds = 10.0;
    // Upper bound of the CPU-side store used for cross-device sharing.
    private const long XDevStoreBytes = 1024L * 1024 * 1024;

    public static SharedBitmapCache<ID2D1Bitmap> Get(IGraphicsDevicesAndContext devices)
    {
        var c = s_cache;
        if (c is not null) return c;
        lock (Gate)
        {
            if (s_cache is not null) return s_cache;

            ulong? budget = QueryBudget(devices);
            long bytes = CachePolicy.DefaultBytes(budget);
            int count = CachePolicy.DefaultCount;
            c = new SharedBitmapCache<ID2D1Bitmap>(count, bytes, b =>
            {
                try { b.Dispose(); } catch { }
            }, deviceGraceSec: DeviceGraceSeconds);
            c.BecameIdle += XDevIdle;   // once every source is disposed, empty the CPU-side store too
            s_cache = c;
            return c;
        }
    }

    // ---- Cross-device sharing: compose once, copy to other devices via CPU memory ----
    private static readonly Lazy<CrossDeviceStore<PixelBlock>> s_xstore =
        new(() => new CrossDeviceStore<PixelBlock>(XDevStoreBytes));
    private static volatile bool s_xdevBroken;

    /// <summary>The store to use for this source's composes, or null if cross-device sharing does not apply (each device then composes on its own).</summary>
    public static CrossDeviceStore<PixelBlock>? XStoreFor(SharedBitmapCache<ID2D1Bitmap> cache)
    {
        if (s_xdevBroken) return null;
        // ParallelRender sets this while its worker devices render at once, before they have all registered here.
        bool multi = AppDomain.CurrentDomain.GetData("ParallelRender.MultiDeviceExport") is true;
        return multi || cache.DeviceCount >= 2 ? s_xstore.Value : null;
    }

    /// <summary>After a readback/upload failure, cross-device sharing is off for the rest of the process.</summary>
    public static void XDevFailed(string why)
    {
        s_xdevBroken = true;
        if (s_xstore.IsValueCreated) s_xstore.Value.Clear();
        try { YukkuriMovieMaker.Commons.Log.Default.Write("FastPsdTachie: cross-device sharing disabled: " + why); } catch { }
    }

    public static void XDevIdle() { if (s_xstore.IsValueCreated) s_xstore.Value.Clear(); }

    private static IDXGIAdapter3? Adapter3(IGraphicsDevicesAndContext devices)
    {
        var adapter = devices.DXGI?.Adapter;
        return adapter?.QueryInterfaceOrNull<IDXGIAdapter3>();
    }

    private static ulong? QueryBudget(IGraphicsDevicesAndContext devices)
    {
        try
        {
            using var a3 = Adapter3(devices);
            if (a3 is null) return null;
            var m = a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return m.Budget;
        }
        catch { return null; }
    }

    /// <summary>
    /// Halves the cache when VRAM usage exceeds ~90% of the budget. Called by each source every few dozen frames;
    /// at most once per 2 s so it cannot shrink repeatedly in a burst.
    /// </summary>
    public static void CheckVram(IGraphicsDevicesAndContext devices)
    {
        var c = s_cache;
        if (c is null) return;
        try
        {
            using var a3 = Adapter3(devices);
            if (a3 is null) return;
            var m = a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            // With ParallelRender running, use the dedicated GPU memory it measures via Windows counters: DXGI CurrentUsage double-counts
            // shared slots opened on the worker devices and saturates near the budget. Otherwise the DXGI value is used.
            long best = AppDomain.CurrentDomain.GetData("ParallelRender.VramUsage") is Func<long> f ? f() : -1;
            ulong usage = best >= 0 ? (ulong)best : m.CurrentUsage;
            if (m.Budget > 0 && usage > m.Budget * 0.90) Shrink(c);
        }
        catch { /* query unavailable: do nothing */ }
    }

    /// <summary>Shrinks on bitmap creation failure (possibly E_OUTOFMEMORY).</summary>
    public static void OnCreateFailure()
    {
        var c = s_cache;
        if (c is not null) Shrink(c);
    }

    private static void Shrink(SharedBitmapCache<ID2D1Bitmap> c)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref s_lastShrinkTicks);
        if (last != 0 && now - last < 2 * Stopwatch.Frequency) return;
        if (Interlocked.CompareExchange(ref s_lastShrinkTicks, now, last) != last) return;
        c.Shrink();
    }
}
