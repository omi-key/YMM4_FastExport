using YukkuriMovieMaker.Plugin.Update;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Project;

namespace ParallelRender;

/// <summary>
/// Parallel render + NVENC output plugin.
/// Default: obtains the Scene and draws video on P independent (GraphicsDevices, TimelineSource) sets, feeding NVENC in order (ParallelWriter).
/// When disabled (PR_DISABLE=1 / setting) or when no Scene can be found, falls back to sequential output through the same encoder (SequentialWriter).
/// A failure after parallel rendering has started deletes the partial output and throws; it never silently switches to sequential.
/// </summary>
public sealed class ParallelRenderPlugin : IVideoFileWriterPlugin
{
    private readonly ParallelSettings _settings = ParallelSettings.Load();

    public string Name => "ParallelRender (並列描画 + NVENC)";

    public PluginDetailsAttribute Details => new()
    {
        AuthorName = "omi",
        ContentId = "ParallelRenderNvenc",
    };

    public IPluginUpdater? Updater => null;

    public VideoFileWriterOutputPath OutputPathMode => VideoFileWriterOutputPath.File;

    public string GetFileExtention() => ".mp4";

    public System.Windows.UIElement GetVideoConfigView(string projectName, VideoInfo videoInfo, int length)
    {
        SceneLocator.NoteConfig(projectName, videoInfo, length);
        return new ConfigView(_settings);
    }

    public bool NeedDownloadResources() => false;

    public Task DownloadResources(ProgressMessage progress, CancellationToken token) => Task.CompletedTask;

    public IVideoFileWriter CreateVideoFileWriter(string path, VideoInfo videoInfo)
    {
        Log.Write($"CreateVideoFileWriter path={path} {videoInfo.Width}x{videoInfo.Height}@{videoInfo.FPS} Hz={videoInfo.Hz}");
        var s = _settings.WithEnvironment();
        if (!s.Enabled)
        {
            Log.Write("ParallelRender disabled -> sequential writer");
            return new SequentialWriter(path, videoInfo, s);
        }
        var scene = SceneLocator.Locate(videoInfo, out var how);
        if (scene == null)
        {
            Log.Write($"No Scene available, falling back to sequential output (how={how})");
            return new SequentialWriter(path, videoInfo, s);
        }
        Log.Write($"Scene via {how}");
        return new ParallelWriter(path, videoInfo, s, scene);
    }
}
