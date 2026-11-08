using System.IO;
using System.Collections.Immutable;
using System.Numerics;
using PsdParser;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.FileSource.Psd;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Plugin.Tachie.Psd;

namespace FastPsdTachiePlugin;

/// <summary>
/// Own implementation of the standard PsdTachieSource mapping (input -> enabled-layer set -> composite). Composed bitmaps are
/// kept in an LRU per enabled-layer set, so returning to a known set needs neither recomposition nor a full layer scan.
/// Anything it cannot decide is forwarded to the standard source (the inner one).
/// The host reads Output once at creation and keeps it, so Output is the fixed output of an AffineTransform2D whose input image is swapped.
/// </summary>
internal sealed class LruPsdTachieSource : ITachieSource2
{
    private static int s_nextId;

    private readonly IGraphicsDevicesAndContext _devices;
    private readonly Func<ITachieSource> _createInner;
    private readonly int _id = Interlocked.Increment(ref s_nextId);
    private readonly DisposeCollector _disposer = new();
    private readonly AffineTransform2D _outputEffect;
    private readonly ID2D1Bitmap _emptyBitmap;
    private readonly SharedBitmapCache<ID2D1Bitmap> _cache;
    private readonly int _deviceId;

    private ITachieSource? _inner;
    private ITachieSource2? _inner2;

    // PSD state
    private PsdDocHandle? _docHandle;   // parsed PSD, possibly shared; composing must hold Doc.ComposeGate
    private PsdFile? _psd;
    private PsdFolder? _root;
    private PsdFileSettings? _settings;
    private ImmutableList<string> _defaultEnable = ImmutableList<string>.Empty;
    private ImmutableList<string>? _lastIds, _lastPaths;
    private ImmutableList<string> _resolved = ImmutableList<string>.Empty;
    private AnimationPlan? _plan;
    private PlanIdentity? _identity;
    private int _planCounter;
    private string? _filePath;
    private bool _isFirst = true;
    private (DateTime, long) _fileStamp;
    private string _normalizedPath = "";

    // Entries on display (current) and just before (previous); both stay pinned in the shared cache.
    private CacheEntry<ID2D1Bitmap>? _current, _previous;
    private bool _showingInner;
    private bool _showingEmpty;
    private bool _disabled;
    private bool _disposed;
    private long _frames;

    public ID2D1Image Output { get; }

    public LruPsdTachieSource(IGraphicsDevicesAndContext devices, Func<ITachieSource> createInner)
    {
        _devices = devices;
        _createInner = createInner;
        _cache = SharedCaches.Get(devices);
        _deviceId = DeviceTokens.IdOf(devices);
        _cache.Register(_deviceId);

        _emptyBitmap = devices.DeviceContext.CreateEmptyBitmap();
        _disposer.Collect(_emptyBitmap);
        _outputEffect = new AffineTransform2D(devices.DeviceContext);
        _disposer.Collect(_outputEffect);
        _outputEffect.SetInput(0, _emptyBitmap, true);
        Output = _outputEffect.Output;
        _disposer.Collect(Output);
    }

    private ITachieSource2 EnsureInner()
    {
        if (_inner2 is null)
        {
            _inner = _createInner();
            _inner2 = (ITachieSource2)_inner;
        }
        return _inner2;
    }

    // ------------------------------------------------------------------ Update

    public void Update(TachieSourceDescription desc)
    {
        _cache.Tick();
        if ((++_frames & 63) == 0) SharedCaches.CheckVram(_devices);   // check VRAM every few dozen frames

        bool handled = false;
        if (!_disabled)
        {
            try { handled = UpdateOwn(desc); }
            catch { _disabled = true; }   // never retry the own path after a failure; forward from now on
        }
        if (!handled) ForwardToInner(desc);
    }

    /// <summary>Returns true if handled here, false if the frame must be forwarded to the standard source.</summary>
    private bool UpdateOwn(TachieSourceDescription desc)
    {
        if (!PsdAccess.Available) return false;

        var tachie = desc.Tachie;
        var faceDesc = tachie.Faces?.FirstOrDefault();   // the standard source also uses only the first face
        var faceParam = faceDesc?.FaceParameter;
        if (tachie.CharacterParameter is not PsdTachieCharacterParameter cp) return false;
        var itemObj = tachie.ItemParameter;
        if (!PsdAccess.IsItem(itemObj)) return false;
        var item = (IPsdTachieParameter)itemObj!;
        if (faceParam is not null && !PsdAccess.IsFace(faceParam)) return false;
        var face = faceParam as IPsdTachieParameter;

        double volume = desc.VoiceVolume;
        if (PsdAccess.HiddenWhenNoSpeech(itemObj!) && volume == -1.0)
        {
            ShowEmpty();
            return true;
        }

        string? text = cp.FilePath;
        if (text is null) return false;

        EyeMode eyeMode = EyeMode.Default;
        MouthMode mouthMode = MouthMode.VowelPriority;
        bool faceEnabled = false;
        if (face is not null)
        {
            faceEnabled = PsdAccess.FaceIsEnabled(face);
            if (!PsdAccess.TryEye(face, out eyeMode) || !PsdAccess.TryMouth(face, out mouthMode)) return false;
        }

        // (Re)load the file
        if (_isFirst || text != _filePath || FileChanged(text))
        {
            LoadFile(text);
            _isFirst = false;
            _filePath = text;
        }
        if (_psd is null || _root is null || _settings is null)
        {
            ShowEmpty();
            return true;
        }

        // Base enabled-layer ids, selected by the same rule as the standard source
        ImmutableList<string>? ids, paths;
        if (face is not null && faceEnabled && text == face.FilePath) { ids = face.EnableLayers; paths = face.EnableLayerPaths; }
        else if (face is not null && faceEnabled) { ids = null; paths = null; }
        else if (text == item.FilePath) { ids = item.EnableLayers; paths = item.EnableLayerPaths; }
        else { ids = null; paths = null; }

        ImmutableList<string> enable;
        if (ids is null) enable = _defaultEnable;
        else
        {
            if (!ReferenceEquals(_lastIds, ids) || !ReferenceEquals(_lastPaths, paths))
            {
                _resolved = _root.ResolveLayers(ids, paths ?? ImmutableList<string>.Empty);
                _lastIds = ids;
                _lastPaths = paths;
            }
            enable = _resolved;
        }

        var plan = _plan;
        var st = _settings;
        if (plan is null || !ReferenceEquals(plan.BaseRef, enable)
            || !ReferenceEquals(plan.EyeListRef, st.EyeAnimations)
            || !ReferenceEquals(plan.MouthListRef, st.MouthAnimations)
            || !ReferenceEquals(plan.VowelListRef, st.MouthVowelAnimations))
        {
            plan = new AnimationPlan(++_planCounter, _root, enable, st, text);
            _plan = plan;
            _identity = new PlanIdentity(_deviceId, _normalizedPath, _fileStamp.Item1.Ticks, _fileStamp.Item2,
                _psd.Header.Width, _psd.Header.Height, plan.Fp1, plan.Fp2);
        }

        var picks = plan.Pick(mouthMode, eyeMode, volume, cp.MouthSensitivity, desc.MouthShape, desc.ItemPosition.Time);
        var key = new StateKey(_identity!, picks);

        var entry = _cache.TryAcquire(key);
        if (entry is not null)
        {
            ShowEntry(entry);
        }
        else
        {
            var root = _root!; var psd = _psd!; var doc = _docHandle!.Doc;
            var xstore = SharedCaches.XStoreFor(_cache);
            var xkey = new StateKey(_identity!.DeviceFree, picks);
            entry = _cache.Acquire(key, () => Compose(doc, root, psd, plan, picks, xstore, xkey), out _);
            if (entry is null) ShowEmpty();
            else ShowEntry(entry);
        }
        return true;
    }

    // ------------------------------------------------------------------ file

    private int _statCountdown;

    /// <summary>Checks whether the file changed, once every ~60 frames.</summary>
    private bool FileChanged(string path)
    {
        if (_psd is null) return false;
        if (--_statCountdown > 0) return false;
        _statCountdown = 60;
        return PsdDocs.Stamp(path) != _fileStamp;
    }

    private void LoadFile(string path)
    {
        ReleasePsd();
        try
        {
            var h = PsdDocs.Acquire(path);
            if (h is not null)
            {
                try
                {
                    var settings = PsdFileSettings.LoadFromPsdFilePath(path);
                    var d = h.Doc;
                    _fileStamp = (d.Stamp, d.Length);
                    _normalizedPath = d.NormalizedPath;
                    _defaultEnable = d.DefaultEnable;
                    _docHandle = h; _psd = d.Psd; _settings = settings; _root = d.Root;
                }
                catch
                {
                    PsdDocs.Release(h);
                    throw;
                }
            }
        }
        catch
        {
            _psd = null; _root = null; _settings = null; _docHandle = null;
        }
    }

    private void ReleasePsd()
    {
        // The file changed: drop what is on display; stale cache entries are purged when the new version is composed.
        DropDisplay();
        _plan = null;
        _lastIds = _lastPaths = null;
        _resolved = ImmutableList<string>.Empty;
        _root = null;
        _settings = null;
        _psd = null;
        PsdDocs.Release(_docHandle);
        _docHandle = null;
    }

    // ------------------------------------------------------------------ cache

    /// <summary>Called on a shared-cache miss (serialized process-wide): sets the enabled layers and composes.</summary>
    private (ID2D1Bitmap? Value, long Bytes) Compose(PsdDoc doc, PsdFolder root, PsdFile psd, AnimationPlan plan, string?[] picks,
        CrossDeviceStore<PixelBlock>? xstore, StateKey xkey)
    {
        // If another device already composed this state, copy its pixels (faster than composing). We hold the compose lock,
        // so no same-state compose runs concurrently.
        if (xstore is not null && xstore.TryGet(xkey) is { } blk)
        {
            try
            {
                var up = BitmapTransfer.Upload(_devices, blk);
                return (up, EstimateBytes(up));
            }
            catch (Exception ex)
            {
                SharedCaches.XDevFailed("upload: " + ex.GetType().Name + ": " + ex.Message);
                xstore = null;
            }
        }
        var set = plan.BuildFinalSet(picks);
        ID2D1Bitmap? bmp;
        lock (doc.ComposeGate)   // PsdFolder enabled flags and the PsdFile read position are shared with other sources
        {
            root.SetActiveLayers(set);   // takes a HashSet, so O(layer count)
            bmp = PsdFileSourcePlugin.CreateBitmap(_devices, root, psd.Header.Width, psd.Header.Height);
        }
        if (bmp is null) { SharedCaches.OnCreateFailure(); return (null, 0); }
        if (xstore is not null)
        {
            try
            {
                var b = BitmapTransfer.Readback(_devices, bmp);
                xstore.Put(xkey, b, b.Bytes);
            }
            catch (Exception ex)
            {
                SharedCaches.XDevFailed("readback: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        return (bmp, EstimateBytes(bmp));
    }

    private static long EstimateBytes(ID2D1Bitmap bmp)
    {
        var px = bmp.PixelSize;
        int bpp;
        try
        {
            bpp = bmp.PixelFormat.Format switch
            {
                Vortice.DXGI.Format.B8G8R8A8_UNorm or Vortice.DXGI.Format.R8G8B8A8_UNorm => 4,
                Vortice.DXGI.Format.R32G32B32A32_Float => 16,
                _ => 8,
            };
        }
        catch { bpp = 8; }
        return (long)px.Width * px.Height * bpp;
    }

    /// <summary>Drops the pins (current / previous); the shared cache owns the images.</summary>
    private void ReleasePins()
    {
        _cache.Release(_current);
        _cache.Release(_previous);
        _current = _previous = null;
    }

    private void DropDisplay()
    {
        if (!_showingInner && !_showingEmpty) ShowEmptyRaw();
        ReleasePins();
    }

    // ------------------------------------------------------------------ display switching

    /// <summary>e carries one pin from acquisition; if it is already on display, the extra pin is returned.</summary>
    private void ShowEntry(CacheEntry<ID2D1Bitmap> e)
    {
        if (ReferenceEquals(_current, e) && !_showingInner && !_showingEmpty)
        {
            _cache.Release(e);
            return;
        }
        var size = e.Value.Size;
        _outputEffect.SetInput(0, e.Value, true);
        _outputEffect.TransformMatrix = Matrix3x2.CreateTranslation(-size.Width / 2f, -size.Height / 2f);
        _cache.Release(_previous);
        _previous = _current;
        _current = e;
        _showingInner = false;
        _showingEmpty = false;
    }

    private void ShowEmpty()
    {
        if (_showingEmpty) return;
        ShowEmptyRaw();
        _cache.Release(_previous);
        _previous = _current;
        _current = null;
    }

    private void ShowEmptyRaw()
    {
        _outputEffect.SetInput(0, _emptyBitmap, true);
        _outputEffect.TransformMatrix = Matrix3x2.Identity;
        _showingEmpty = true;
        _showingInner = false;
    }

    private void ForwardToInner(TachieSourceDescription desc)
    {
        var inner = EnsureInner();
        inner.Update(desc);
        ShowInner(inner);
    }

    private void ShowInner(ITachieSource inner)
    {
        if (_showingInner) return;
        _outputEffect.SetInput(0, inner.Output, true);
        _outputEffect.TransformMatrix = Matrix3x2.Identity;   // the standard Output is already centred
        _cache.Release(_previous);
        _previous = _current;
        _current = null;
        _showingInner = true;
        _showingEmpty = false;
    }

    // ------------------------------------------------------------------ legacy signature / Dispose

    public void Update(TimeSpan tachieTime, TimeSpan tachieLength, TimeSpan faceTime, TimeSpan faceLength,
        ITachieCharacterParameter characterParameter, ITachieItemParameter itemParameter, ITachieFaceParameter faceParameter, double kuchipaku)
    {
        EnsureInner();
        _inner!.Update(tachieTime, tachieLength, faceTime, faceLength, characterParameter, itemParameter, faceParameter, kuchipaku);
        ShowInner(_inner);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _outputEffect.SetInput(0, null, true);
            ReleasePins();
            _psd = null;
            PsdDocs.Release(_docHandle);
            _docHandle = null;
        }
        finally
        {
            _disposer.Dispose();
            _inner?.Dispose();
            _cache.Unregister(_deviceId);   // the last source empties the shared cache
        }
    }
}
