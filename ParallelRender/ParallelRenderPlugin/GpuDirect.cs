using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using YukkuriMovieMaker.Commons;

namespace ParallelRender;

/// <summary>
/// One shared texture for GPU-direct hand-off. Created on the encoder's device (the owner) and opened on each
/// worker's device through an NT handle.
/// Keyed mutex: key 0 = the drawing side may take it (free), key 1 = the encoder side may take it (drawn).
///   Worker: Acquire(0) -> draw -> Release(1). Encoder: Acquire(1) -> CopyResource (inside NvencEncode) -> Release(0).
/// </summary>
internal sealed class SharedSlot : IDisposable
{
    internal ID3D11Texture2D? OwnerTex;
    internal IDXGIKeyedMutex? OwnerMutex;
    internal IntPtr Handle;
    internal readonly WorkerView?[] Views;
    /// <summary>Worker that opens this slot. DXGI counts usage once per device that opens a resource, so a slot is opened by the owner plus one worker only.</summary>
    internal int Affinity;

    internal SharedSlot(int workers) { Views = new WorkerView?[workers]; }

    public IntPtr OwnerTexturePtr => OwnerTex!.NativePointer;

    /// <summary>Encoder side: waits for "drawn" (key 1) and acquires.</summary>
    public void OwnerAcquire()
    {
        OwnerMutex!.AcquireSync(1UL, SharedFramePool.SyncTimeoutMs);
    }

    /// <summary>Encoder side: once the copy is queued, hands the slot back as free (key 0).</summary>
    public void OwnerRelease() => OwnerMutex!.ReleaseSync(0UL);

    public void Dispose()
    {
        // Each worker's view is disposed first, on that worker's thread, by Renderer.Dispose.
        OwnerMutex?.Dispose(); OwnerMutex = null;
        OwnerTex?.Dispose(); OwnerTex = null;
        if (Handle != IntPtr.Zero) { SharedFramePool.CloseHandle(Handle); Handle = IntPtr.Zero; }
    }
}

/// <summary>A shared texture opened on a worker's device (D2D target bitmap plus keyed mutex).</summary>
internal sealed class WorkerView : IDisposable
{
    public ID3D11Texture2D Tex = null!;
    public IDXGIKeyedMutex Mutex = null!;
    public ID2D1Bitmap1 Bitmap = null!;
    public long Bytes;   // amount DXGI double-counts (added to Vram.Overcount); returned on Dispose
    public void Dispose()
    {
        if (Bytes != 0) { Vram.AddOvercount(-Bytes); Bytes = 0; }
        try { Bitmap?.Dispose(); } catch { }
        try { Mutex?.Dispose(); } catch { }
        try { Tex?.Dispose(); } catch { }
    }
}

/// <summary>
/// Pool of shared textures (VRAM). Slots are created on demand up to a cap; Rent/Return are thread-safe.
/// On a creation failure Rent returns null (the caller falls back to CPU readback), sets Pressure and lowers the cap to the current count.
/// </summary>
internal sealed class SharedFramePool : IDisposable
{
    public const int SyncTimeoutMs = -1;   // INFINITE: AcquireSync returns void here, so a timeout could not be detected anyway

    private readonly ID3D11Device _owner;
    private readonly int _w, _h, _workers;
    private readonly object _lk = new();
    private readonly Stack<SharedSlot>[] _free;
    private int _reserved;
    private readonly List<SharedSlot> _all = new();
    private int _cap;
    private volatile bool _pressure;
    private string? _lastError;
    public int Created { get { lock (_lk) return _all.Count; } }
    public bool Pressure => _pressure;
    public string? LastError => _lastError;
    public long SlotBytes => (long)_w * _h * 4;

    /// <summary>owner = the encoder's device.</summary>
    public SharedFramePool(ID3D11Device owner, int w, int h, int workers)
    {
        _w = w; _h = h; _workers = workers;
        _free = Enumerable.Range(0, workers).Select(_ => new Stack<SharedSlot>()).ToArray();
        _owner = owner;
    }

    /// <summary>Maximum number of slots; must be set before the first Rent.</summary>
    public void SetCap(int cap) { lock (_lk) _cap = Math.Max(0, cap); }

    /// <summary>VRAM guard: no new slot is created once the process's dedicated GPU memory would exceed this fraction of the budget. Kept below the 90% threshold at which FastPsdTachie shrinks its cache.</summary>
    public const double VramGuard = 0.85;

    /// <summary>Returns a free slot for worker wi, creating one if under the cap and the VRAM guard; null makes the caller read this frame back through the CPU.</summary>
    public SharedSlot? Rent(int wi)
    {
        lock (_lk)
        {
            if (_free[wi].Count > 0) return _free[wi].Pop();
            if (_reserved >= _cap) return null;
            _reserved++;
        }
        if (Vram.Query() is { } v && v.budget > 0 && v.usage + (ulong)SlotBytes > v.budget * VramGuard)
        {
            lock (_lk) _reserved--;
            return null;
        }
        SharedSlot s;
        try { s = Create(); }
        catch (Exception ex)
        {
            lock (_lk) { _lastError = ex.GetType().Name + ": " + ex.Message; _pressure = true; _reserved--; _cap = _reserved; }
            Log.Write($"SharedFramePool: slot create failed (created={_reserved}): {ex.Message}");
            return null;
        }
        s.Affinity = wi;
        lock (_lk) _all.Add(s);
        return s;
    }

    /// <summary>Drops a slot this worker could not open (its cap entry is freed).</summary>
    public void Discard(SharedSlot s)
    {
        lock (_lk) { if (_all.Remove(s)) _reserved--; }
        DisposeSlotSafe(s);
    }

    public void Return(SharedSlot s) { lock (_lk) _free[s.Affinity].Push(s); }

    private SharedSlot Create()
    {
        var desc = new Texture2DDescription
        {
            Width = _w, Height = _h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.SharedKeyedMutex | ResourceOptionFlags.SharedNTHandle,
        };
        var s = new SharedSlot(_workers);
        try
        {
            s.OwnerTex = _owner.CreateTexture2D(desc);
            s.OwnerMutex = s.OwnerTex.QueryInterface<IDXGIKeyedMutex>();
            using (var res = s.OwnerTex.QueryInterface<IDXGIResource1>())
                s.Handle = res.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
            Vram.NoteSlotCreated((long)_w * _h * 4);
            return s;
        }
        catch { s.Dispose(); throw; }
    }

    /// <summary>Opens the slot on worker wi's device (first time only; call from the worker's thread).</summary>
    public static WorkerView OpenView(SharedSlot slot, int wi, IGraphicsDevicesAndContext ctx)
    {
        if (slot.Views[wi] is { } v) return v;
        var view = new WorkerView();
        try
        {
            using var dev1 = ctx.D3D.Device.QueryInterface<ID3D11Device1>();
            view.Tex = dev1.OpenSharedResource1<ID3D11Texture2D>(slot.Handle) ?? throw new InvalidOperationException("共有テクスチャをワーカーのデバイスで開けませんでした");
            view.Mutex = view.Tex.QueryInterface<IDXGIKeyedMutex>();
            { var td = view.Tex.Description; view.Bytes = (long)td.Width * td.Height * 4; Vram.AddOvercount(view.Bytes); }
            using var surface = view.Tex.QueryInterface<IDXGISurface>();
            var props = new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
            view.Bitmap = ctx.DeviceContext.CreateBitmapFromDxgiSurface(surface, props);
        }
        catch { view.Dispose(); throw; }
        slot.Views[wi] = view;
        return view;
    }

    private static void DisposeSlotSafe(SharedSlot s) { try { s.Dispose(); } catch (Exception ex) { Log.Write("slot dispose: " + ex.Message); } }

    public void Dispose()
    {
        List<SharedSlot> all;
        lock (_lk) { all = _all.ToList(); _all.Clear(); foreach (var f in _free) f.Clear(); }
        foreach (var s in all) DisposeSlotSafe(s);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr h);
}

/// <summary>VRAM (local segment) usage and budget of this process (DXGI QueryVideoMemoryInfo).</summary>
internal static class Vram
{
    private static long _overcount;
    /// <summary>Amount DXGI CurrentUsage double-counts: the views of shared slots opened on worker devices.</summary>
    public static long Overcount => Volatile.Read(ref _overcount);
    public static void AddOvercount(long bytes) => Interlocked.Add(ref _overcount, bytes);
    private static long _slotBytesCreated;
    /// <summary>Total bytes of shared slots created so far (owner textures); GpuMemoryProbe adds those created after its last sample.</summary>
    public static long SlotBytesCreated => Volatile.Read(ref _slotBytesCreated);
    public static void NoteSlotCreated(long bytes) => Interlocked.Add(ref _slotBytesCreated, bytes);
    /// <summary>Best usage estimate in bytes: dedicated GPU memory from the Windows counter, else DXGI minus the double-count. Published as AppDomain data "ParallelRender.VramUsage" for FastPsdTachie's cache shrink decision.</summary>
    public static long BestUsage() => Query() is { } q ? (long)q.usage : -1;
    static Vram() { AppDomain.CurrentDomain.SetData("ParallelRender.VramUsage", (Func<long>)BestUsage); GpuMemoryProbe.EnsureStarted(); }
    private static IDXGIAdapter3? _a3;
    private static bool _tried;
    private static readonly object Lk = new();

    public static (ulong usage, ulong budget)? Query()
    {
        try
        {
            lock (Lk)
            {
                if (!_tried)
                {
                    _tried = true;
                    DXGI.CreateDXGIFactory1(out IDXGIFactory1? f).CheckError();
                    using (f)
                    {
                        f!.EnumAdapters1(0, out var ad).CheckError();
                        _a3 = ad.QueryInterfaceOrNull<IDXGIAdapter3>();
                        ad.Dispose();
                    }
                }
                if (_a3 is null) return null;
                var m = _a3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                var b = m.Budget;
                ulong dxgi = m.CurrentUsage - (ulong)Math.Min((long)m.CurrentUsage, Overcount);
                return (GpuMemoryProbe.UsedBytes ?? dxgi, b);
            }
        }
        catch { return null; }
    }
}

/// <summary>Free physical memory (window budget on the CPU-readback path).</summary>
internal static class SysMem
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus s);

    public static (ulong total, ulong avail) Query()
    {
        var s = new MemStatus { dwLength = (uint)Marshal.SizeOf<MemStatus>() };
        return GlobalMemoryStatusEx(ref s) ? (s.ullTotalPhys, s.ullAvailPhys) : (0, 0);
    }
}
