using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;

namespace ParallelRender;

/// <summary>
/// One worker's rendering set: GraphicsDevices, DeviceContext, TimelineSource and output bitmaps.
/// The draw sequence mirrors the host's own export. A Renderer is used from a single thread only.
/// </summary>
internal sealed class Renderer : IDisposable
{
    private readonly Scene _scene;
    private readonly GraphicsDevices _devices;
    private readonly IGraphicsDevicesAndContext _ctx;
    private readonly TimelineSource _source;
    private ID2D1Bitmap1? _target;
    private ID2D1Bitmap1? _readable;
    private readonly int _wi;
    private readonly List<SharedSlot> _openedSlots = new();
    private readonly nint _d3dKey;   // device registered with VideoDecodeLink
    private bool _textCacheFailed;
    private TextCache? _textCache;

    /// <summary>With cpuPath=false no CPU-readback bitmaps are created (GPU direct only; call EnsureCpuPath if needed).</summary>
    public Renderer(Scene scene, int workerIndex, bool cpuPath, bool textCache)
    {
        _scene = scene; _wi = workerIndex;
        _devices = new GraphicsDevices();
        _ctx = _devices.CreateContext();
        // Lets VideoDecode recognise this as a worker device (and use direct decoding on it).
        _d3dKey = _ctx.D3D.Device.NativePointer; VideoDecodeLink.Register(_d3dKey);
        _source = new TimelineSource(_ctx, scene, null);
        if (textCache) _textCache = new TextCache(_source, _ctx, scene.Timeline.VideoInfo.Width, scene.Timeline.VideoInfo.Height);
        if (cpuPath) EnsureCpuPath();
    }

    /// <summary>Creates the CPU-readback path (a render-target bitmap and a CpuRead bitmap).</summary>
    public void EnsureCpuPath()
    {
        if (_target != null) return;
        var vi = _scene.Timeline.VideoInfo;
        var dc = _ctx.DeviceContext;
        _target = dc.CreateNotInitializedBitmap(vi.Width, vi.Height, BitmapOptions.Target | BitmapOptions.CannotDraw);
        _readable = dc.CreateNotInitializedBitmap(vi.Width, vi.Height, BitmapOptions.CannotDraw | BitmapOptions.CpuRead);
    }

    /// <summary>TimelineSource.Update, i.e. everything up to recording the command list.</summary>
    public void UpdateStage(int frame)
    {
        var vi = _scene.Timeline.VideoInfo;
        _source.Update(vi.GetTimeFrom(frame), TimelineSourceUsage.Exporting);
        ProcessTextCache(vi.GetTimeFrom(frame));
    }

    /// <summary>Subtitle reuse. If the host's internals (accessed via the publicizer) differ from what we expect, switch to the standard path for good.</summary>
    private void ProcessTextCache(TimeSpan time)
    {
        if (_textCache is null || _textCacheFailed) return;
        try { if (_textCache.Process()) { _source.Update(time, TimelineSourceUsage.Exporting); _textCache.Process(); } }
        catch (Exception ex)
        {
            Log.Write($"textcache failed, falling back to the standard path: {ex}");
            try { _textCache.BypassAll(); } catch { }
            _textCacheFailed = true;
        }
    }

    /// <summary>BeginDraw..EndDraw: submits the command list to D2D.</summary>
    public void DrawStage() => DrawTo(_target!);

    private void DrawTo(ID2D1Bitmap1 target)
    {
        var vi = _scene.Timeline.VideoInfo;
        var dc = _ctx.DeviceContext;
        dc.Target = target;
        dc.BeginDraw();
        dc.Clear(new Color4(0f, 0f, 0f, 0f));
        dc.DrawImage(_source.Output, new Vector2(vi.Width / 2f, vi.Height / 2f));
        dc.EndDraw();
        dc.Target = null;
    }

    /// <summary>Opens the shared texture on this device (first time only). Throws on failure so the caller can fall back to the CPU path.</summary>
    public WorkerView OpenSlot(SharedSlot slot)
    {
        var v = slot.Views[_wi];
        if (v is null)
        {
            v = SharedFramePool.OpenView(slot, _wi, _ctx);
            _openedSlots.Add(slot);
        }
        return v;
    }

    /// <summary>
    /// GPU direct: takes a shared texture (key 0 = free), draws straight into it and hands it over with key 1 (drawn).
    /// The keyed-mutex Acquire/Release must stay outside BeginDraw..EndDraw.
    /// </summary>
    public void DrawStageShared(SharedSlot slot)
    {
        var v = OpenSlot(slot);
        v.Mutex.AcquireSync(0UL, SharedFramePool.SyncTimeoutMs);
        try { DrawTo(v.Bitmap); }
        finally
        {
            _ctx.DeviceContext.Target = null;
            v.Mutex.ReleaseSync(1UL);
        }
    }

    /// <summary>Waits for the GPU and reads the last drawn frame (BGRA) into dst (at least width*height*4 bytes).</summary>
    public void ReadbackInto(byte[] dst)
    {
        _readable!.CopyFromBitmap(_target!);
        var buf = dst;
        _readable.ToBytes(ref buf);
        if (!ReferenceEquals(buf, dst)) throw new InvalidOperationException("読み戻しバッファのサイズが合わない");
    }

    public void Dispose()
    {
        foreach (var sl in _openedSlots)
        {
            try { sl.Views[_wi]?.Dispose(); sl.Views[_wi] = null; } catch { }
        }
        _openedSlots.Clear();
        _readable?.Dispose();
        _target?.Dispose();
        _textCache?.Dispose();
        _source.Dispose();
        VideoDecodeLink.Unregister(_d3dKey);
        _ctx.Dispose();
        _devices.Dispose();
    }
}
