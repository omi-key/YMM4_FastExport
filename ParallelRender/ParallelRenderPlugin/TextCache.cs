using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Player.Video.Effects;
using YukkuriMovieMaker.Player.Video.Items;
using YukkuriMovieMaker.Plugin;

namespace ParallelRender;

/// <summary>
/// Reuses the post-effect image of subtitles (text / voice subtitles) as one bitmap while their state is unchanged.
/// Not bit-exact: the bitmap is rounded to 8 bit before being composited, so where there are pixels under a subtitle its
/// edge pixels can differ from the standard output by about +-1.
/// D2D re-rasterises a subtitle's outline strokes in every EndDraw. Here the subtitle's DrawingEffect (zoom, crop,
/// Transform3D, opacity) is swapped for a subclass with the same internals; when the input is the plain text command list
/// (no video effects) and the DrawDescription stays identical for a few frames, the effect output is drawn once into a
/// frame-sized bitmap (same tile boundaries as the final draw) and reused. Any state change (fade, move, subtitle switch)
/// returns to the standard path, so the first frames of a new state are always standard.
/// </summary>
internal sealed class TextCache : IDisposable
{
    /// <summary>Frames of identical state before a bitmap is made (nothing is built while a fade changes every frame).</summary>
    private const int StableFrames = 2;
    /// <summary>Maximum bitmaps one worker holds at once.</summary>
    private const int MaxLive = 8;

    private readonly TimelineSource _src;
    private readonly IGraphicsDevicesAndContext _ctx;
    private readonly int _w, _h;
    private readonly Dictionary<SourceOutput, CachedDrawingEffect> _map = new();
    private readonly HashSet<SourceOutput> _live = new();
    private readonly List<SourceOutput> _dead = new();
    private readonly ConcurrentQueue<IDisposable> _retired = new();
    private int _liveBitmaps;
    private int _rasterErrors;   // limits log noise
    private bool _hasFrameBuffer;

    public TextCache(TimelineSource src, IGraphicsDevicesAndContext ctx, int width, int height) { _src = src; _ctx = ctx; _w = width; _h = height; }

    /// <summary>Forces every swapped effect back to the standard output (or releases it).</summary>
    public void BypassAll() { foreach (var w in _map.Values) w.Bypass = true; }

    /// <summary>
    /// Call right after TimelineSource.Update. Returns true when an output that used a swapped effect became unusable this
    /// frame, so the caller must redo Update. Subtitles with a non-normal blend, or frames with clipping against the item
    /// above, are never swapped.
    /// </summary>
    public bool Process()
    {
        while (_retired.TryDequeue(out var d)) { try { d.Dispose(); } catch { } }
        _live.Clear();
        bool redo = false;
        var ordered = _src.GetOrderedTimelineResources().ToList();
        bool allowed = true;
        foreach (var kv in ordered) if (kv.Key.IsClippingWithObjectAbove) { allowed = false; break; }
        _hasFrameBuffer = false;
        foreach (var kv in ordered) if (kv.Value.Source is IFrameBufferSource) { _hasFrameBuffer = true; break; }
        foreach (var kv in ordered)
        {
            var e = kv.Value;
            List<ISourceOutput>? outs; IGraphicsDevicesAndContext? dev;
            switch (e.Source)
            {
                case TextSource t: outs = t.outputs; dev = t.devices; break;
                case JimakuSource j: outs = j.outputs; dev = j.devices; break;
                default: continue;
            }
            bool ok = allowed && kv.Key.Blend == YukkuriMovieMaker.Project.Blend.Normal;
            foreach (var o in outs)
            {
                if (o is not SourceOutput so) continue;
                _live.Add(so);
                if (!_map.TryGetValue(so, out var w))
                {
                    w = new CachedDrawingEffect(dev, so, _retired);
                    so.DrawingEffect = w;   // takes effect from the next Update (EffectedSourceOutput re-attaches its input)
                    _map[so] = w;
                    continue;
                }
                bool served = w.Served; w.Served = false;
                if (ok) w.OkStreak++; else w.OkStreak = 0;
                if (w.CachedImage is not null)
                {
                    if (ok && !(w.OffFrame && _hasFrameBuffer)) continue;
                    w.Invalidate();
                    if (served) redo = true;   // it was already used for this frame
                    continue;
                }
                if (ok && w.OkStreak >= StableFrames && w.Stable >= StableFrames && w.PlainInput && !w.RasterFailed && _liveBitmaps < MaxLive)
                {
                    Raster(w);
                }
            }
        }
        if (_map.Count != _live.Count)
        {
            _dead.Clear();
            foreach (var k in _map.Keys) if (!_live.Contains(k)) _dead.Add(k);
            foreach (var k in _dead) { var w = _map[k]; w.Invalidate(); _retired.Enqueue(w); _map.Remove(k); }
        }
        _liveBitmaps = 0;
        foreach (var w in _map.Values) if (w.Bitmap is not null) _liveBitmaps++;
        return redo;
    }

    private void Raster(CachedDrawingEffect w)
    {
        var dc = _ctx.DeviceContext;
        try
        {
            var img = w.BaseOutput;
            var b = dc.GetImageLocalBounds(img);
            float hw = _w / 2f, hh = _h / 2f;
            // Skip empty outputs. Outputs spilling outside the frame are skipped while a frame-buffer item (an effect applied to the
            // composite below) exists, since later effects may sample the spilled part; pixels inside the frame are identical either way.
            bool inside = b.Left >= -hw && b.Top >= -hh && b.Right <= hw && b.Bottom <= hh;
            if (!(b.Right > b.Left && b.Bottom > b.Top) || !float.IsFinite(b.Left + b.Top + b.Right + b.Bottom) || (!inside && _hasFrameBuffer))
            { w.RasterFailed = true; return; }
            w.OffFrame = !inside;
            var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.Target);
            var bmp = dc.CreateBitmap(new SizeI(_w, _h), IntPtr.Zero, 0, props);
            dc.Target = bmp;
            dc.BeginDraw();
            dc.Clear(new Color4(0f, 0f, 0f, 0f));
            dc.DrawImage(img, new Vector2(hw, hh));   // same call and tile boundaries as the final draw (Renderer.DrawTo)
            dc.EndDraw();
            var list = dc.CreateCommandList();
            dc.Target = list;
            dc.BeginDraw();
            dc.DrawImage(bmp, new Vector2(-hw, -hh));
            dc.EndDraw();
            list.Close();
            dc.Target = null;
            w.Install(bmp, list);
        }
        catch (Exception ex)
        {
            if (Interlocked.Increment(ref _rasterErrors) <= 3) Log.Write($"textcache: raster failed: {ex.ToString().Replace("\n", " ")}");
            try { dc.Transform = Matrix3x2.Identity; dc.Target = null; } catch { }
            w.RasterFailed = true;
        }
    }

    public void Dispose()
    {
        foreach (var w in _map.Values) { w.Invalidate(); w.Dispose(); }
        _map.Clear();
        while (_retired.TryDequeue(out var d)) { try { d.Dispose(); } catch { } }
    }
}

/// <summary>Same as the standard DrawingEffect, but while the state is unchanged its output is replaced by a bitmap (wrapped in a command list).</summary>
internal sealed class CachedDrawingEffect : DrawingEffect, IVideoEffectProcessor
{
    private readonly SourceOutput _so;
    private readonly ConcurrentQueue<IDisposable> _retired;
    public bool Bypass;
    public DrawDescription? Last, CachedState;
    public int Stable;
    public bool PlainInput, RasterFailed, OffFrame, Served;
    public int OkStreak;
    public ID2D1Bitmap1? Bitmap;
    public ID2D1CommandList? CachedImage;

    public CachedDrawingEffect(IGraphicsDevicesAndContext devices, SourceOutput so, ConcurrentQueue<IDisposable> retired) : base(devices) { _so = so; _retired = retired; }

    public override DrawDescription Update(EffectDescription effectDescription)
    {
        var r = base.Update(effectDescription);
        if (Last is not null && r.Equals(Last)) Stable++;
        else { Stable = 0; RasterFailed = false; }
        Last = r;
        PlainInput = ReferenceEquals(input, _so.Output);
        if (CachedImage is not null && (!PlainInput || !r.Equals(CachedState))) Invalidate();
        return r;
    }

    public ID2D1Image BaseOutput => base.Output;

    ID2D1Image IDrawable.Output { get { if (!Bypass && CachedImage is not null) { Served = true; return CachedImage; } return base.Output; } }

    public void Install(ID2D1Bitmap1 bmp, ID2D1CommandList list) { Bitmap = bmp; CachedImage = list; CachedState = Last; }

    /// <summary>Drops the swap. Disposal is deferred to the next Process because the previous frame's command list may still reference the bitmap.</summary>
    public void Invalidate()
    {
        if (CachedImage is not null) _retired.Enqueue(CachedImage);
        if (Bitmap is not null) _retired.Enqueue(Bitmap);
        CachedImage = null; Bitmap = null; CachedState = null;
    }
}
