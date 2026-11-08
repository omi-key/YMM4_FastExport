using Vortice.Direct2D1;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Plugin.Tachie;

namespace FastPsdTachiePlugin;

/// <summary>
/// Wraps the standard PSD tachie source and skips its Update on frames where the inputs are provably identical to the previous
/// frame, returning the same Output. Whenever in doubt it forwards to the inner source, which is the standard behaviour.
/// Used when the standard plugin's internals cannot be reached (see PsdAccess) or LruPsdTachieSource cannot be created.
/// </summary>
internal sealed class FastPsdTachieSource : ITachieSource2
{
    private readonly ITachieSource2 _inner;

    private FrameKey? _last;
    private bool _innerUpdated;
    private bool _skipDisabled;
    private bool _disposed;

    public FastPsdTachieSource(ITachieSource2 inner)
    {
        _inner = inner;
    }

    public ID2D1Image Output => _inner.Output;

    public void Update(TachieSourceDescription desc)
    {
        FrameKey? now = null;
        if (!_skipDisabled)
        {
            try { now = FrameKey.Build(desc); }
            catch
            {
                // Reflection on the parameter types failed: never skip again for this source.
                _skipDisabled = true;
                now = null;
            }
        }

        if (now is not null && _innerUpdated && _last is not null && _last.SameAs(now)) return;

        // Forward. If the inner Update throws, _last stays null so the next frame is forwarded again.
        _last = null;
        _inner.Update(desc);
        _innerUpdated = true;
        _last = now;
    }

    // Legacy ITachieSource signature. The host is expected to use ITachieSource2; if this is called, pass through and drop the state.
    public void Update(TimeSpan tachieTime, TimeSpan tachieLength, TimeSpan faceTime, TimeSpan faceLength,
        ITachieCharacterParameter characterParameter, ITachieItemParameter itemParameter, ITachieFaceParameter faceParameter, double kuchipaku)
    {
        _last = null;
        _inner.Update(tachieTime, tachieLength, faceTime, faceLength, characterParameter, itemParameter, faceParameter, kuchipaku);
        _innerUpdated = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _last = null;
        _inner.Dispose();
    }
}

/// <summary>Everything that can affect the inner source's output for one frame.</summary>
internal sealed class FrameKey
{
    private ParamSnapshot? _character, _item;
    private ParamSnapshot?[] _faces = Array.Empty<ParamSnapshot?>();
    private object? _screen;
    private float _renderScale;
    private object? _usage;
    private Guid _scene;
    private bool _voiceDep;
    private double _volume;
    private object? _mouthShape;

    /// <summary>Returns null when the frame cannot be proven skippable (unknown parameter type, or blink animation active).</summary>
    public static FrameKey? Build(TachieSourceDescription desc)
    {
        var k = new FrameKey();
        var tachie = desc.Tachie;
        bool isUnsafe = false;
        k._character = ParamSnapshot.Capture(tachie.CharacterParameter, ref isUnsafe);
        k._item = ParamSnapshot.Capture(tachie.ItemParameter, ref isUnsafe);
        var faces = new List<ParamSnapshot?>();
        if (tachie.Faces is not null)
            foreach (var f in tachie.Faces) faces.Add(ParamSnapshot.Capture(f, ref isUnsafe));
        k._faces = faces.ToArray();
        if (isUnsafe) return null;

        k._screen = desc.ScreenSize;
        k._renderScale = desc.RenderScale;
        k._usage = desc.Usage;
        k._scene = desc.SceneId;

        bool eyeAnim = false, mouthAnim = false, hidden = k._item?.HiddenWhenNoSpeech == true;
        foreach (var f in k._faces)
        {
            if (f is null) continue;
            eyeAnim |= f.EyeAnimates;
            mouthAnim |= f.MouthAnimates;
            hidden |= f.HiddenWhenNoSpeech;
        }

        // Whether the PSD's settings file actually defines eye/mouth animations is not checked: assume it does.
        // The inner source's blink depends on time (and a per-process seed), so frames with an active eye animation always forward.
        if (eyeAnim) return null;
        if (mouthAnim || hidden) k._voiceDep = true;
        if (k._voiceDep)
        {
            k._volume = desc.VoiceVolume;
            k._mouthShape = desc.MouthShape;
        }
        return k;
    }

    public bool SameAs(FrameKey o)
    {
        if (!Same(_character, o._character) || !Same(_item, o._item) || _faces.Length != o._faces.Length) return false;
        for (int i = 0; i < _faces.Length; i++)
            if (!Same(_faces[i], o._faces[i])) return false;
        if (!Equals(_screen, o._screen) || _renderScale != o._renderScale || !Equals(_usage, o._usage) || _scene != o._scene) return false;
        if (_voiceDep != o._voiceDep) return false;
        return !_voiceDep
            || (BitConverter.DoubleToInt64Bits(_volume) == BitConverter.DoubleToInt64Bits(o._volume) && Equals(_mouthShape, o._mouthShape));
    }

    private static bool Same(ParamSnapshot? a, ParamSnapshot? b) => a is null ? b is null : a.SameAs(b);
}
