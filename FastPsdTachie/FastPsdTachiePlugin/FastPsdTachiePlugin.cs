using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.Tachie;
using YukkuriMovieMaker.Plugin.Tachie.Psd;
using YukkuriMovieMaker.Plugin.Update;

namespace FastPsdTachiePlugin;

/// <summary>
/// Wrapper around the standard PSD tachie plugin. Parameter creation, exo output and script files are delegated unchanged;
/// only the render source is replaced. Type, assembly and namespace names must stay as they are: .ymmp files serialize them.
/// </summary>
public sealed class FastPsdTachiePlugin : ITachiePlugin
{
    private readonly PsdTachiePlugin _inner = new();

    private readonly PluginDetailsAttribute _details = new()
    {
        AuthorName = "FastPsdTachie",
        ContentId = "FastPsdTachiePlugin",
    };

    public string Name => "PSD立ち絵 (高速化版)";

    public PluginDetailsAttribute Details => _details;

    public IPluginUpdater? Updater => null;

    public bool HasScriptFile => _inner.HasScriptFile;

    public ITachieCharacterParameter CreateCharacterParameter() => _inner.CreateCharacterParameter();

    public ITachieItemParameter CreateItemParameter() => _inner.CreateItemParameter();

    public ITachieFaceParameter CreateFaceParameter() => _inner.CreateFaceParameter();

    public ITachieSource CreateTachieSource(IGraphicsDevicesAndContext devices)
    {
        if (PsdAccess.Available)
        {
            try { return new LruPsdTachieSource(devices, () => _inner.CreateTachieSource(devices)); }
            catch { /* fall back to the standard source */ }
        }
        var src = _inner.CreateTachieSource(devices);
        // Unexpected inner implementation: return it unwrapped.
        return src is ITachieSource2 s2 ? new FastPsdTachieSource(s2) : src;
    }

    public IEnumerable<ExoItem> CreateExoItems(
        int FPS,
        IEnumerable<TachieItemExoDescription> tachieItemDescriptions,
        IEnumerable<TachieFaceItemExoDescription> faceItemDescriptions,
        IEnumerable<TachieVoiceItemExoDescription> voiceDescriptions)
        => _inner.CreateExoItems(FPS, tachieItemDescriptions, faceItemDescriptions, voiceDescriptions);

    public void CreateScriptFile(string scriptDirectoryPath) => _inner.CreateScriptFile(scriptDirectoryPath);
}
