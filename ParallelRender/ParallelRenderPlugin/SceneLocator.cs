using System.IO;
using System.Windows;
using System.Windows.Threading;
using YukkuriMovieMaker.CommandLine;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.ViewModels;
using YmmJson = YukkuriMovieMaker.Json.Json;
using YmmProject = YukkuriMovieMaker.Project.Project;

namespace ParallelRender;

/// <summary>
/// IVideoFileWriterPlugin.CreateVideoFileWriter only receives a VideoInfo, so the Scene to export has to be found independently.
/// (1) Headless (--encode &lt;ymmp&gt;): loads the Project the same way the host's command-line encoder does.
/// (2) GUI: takes the private `model` of the main window's MainViewModel (DataContext) and builds the same
///     Scene(model.Timeline, model.Scenes, ...) as the host's export. In the GUI the host calls CreateVideoFileWriter
///     synchronously on the UI thread, so it can be read in place.
/// Returns null if neither works (the caller falls back to sequential output).
/// </summary>
internal static class SceneLocator
{
    public static Scene? Locate(VideoInfo expected, out string how)
    {
        Scene? scene = null;
        how = "none";
        try
        {
            scene = FromCommandLine();
            if (scene != null) how = "commandline";
        }
        catch (Exception ex) { Log.Write("SceneLocator commandline failed: " + ex); }
        if (scene == null)
        {
            try
            {
                scene = FromGui();
                if (scene != null) how = "gui";
            }
            catch (Exception ex) { Log.Write("SceneLocator gui failed: " + ex); }
        }
        if (scene == null) return null;
        // Guard against picking the wrong project: the VideoInfo must match what the host passed.
        var vi = scene.Timeline.VideoInfo;
        if (vi.Width != expected.Width || vi.Height != expected.Height || vi.FPS != expected.FPS || vi.Hz != expected.Hz)
        {
            Log.Write($"SceneLocator: VideoInfo mismatch ({vi.Width}x{vi.Height}@{vi.FPS}/{vi.Hz} vs {expected.Width}x{expected.Height}@{expected.FPS}/{expected.Hz})");
            how = "mismatch";
            return null;
        }
        return scene;
    }

    private static (string name, VideoInfo vi, int length)? _hint;

    /// <summary>Remembers the values the export dialog passed to GetVideoConfigView: a hint for which window is exporting.</summary>
    public static void NoteConfig(string projectName, VideoInfo vi, int length) => _hint = (projectName, vi, length);

    private static Window? Choose(List<Window> candidates)
    {
        if (_hint is { } h)
        {
            var byHint = candidates.Where(w =>
            {
                var m = ((MainViewModel)w.DataContext).model;
                var t = m.Timeline;
                var path = m.ProjectFilePath;
                bool nameOk = string.IsNullOrWhiteSpace(path) || Path.GetFileNameWithoutExtension(path) == h.name;
                return nameOk && t.Length == h.length && t.VideoInfo.Width == h.vi.Width && t.VideoInfo.Height == h.vi.Height && t.VideoInfo.FPS == h.vi.FPS && t.VideoInfo.Hz == h.vi.Hz;
            }).ToList();
            if (byHint.Count == 1) return byHint[0];
            if (byHint.Count > 1) candidates = byHint; // several windows match: pick the active one among them
        }
        return candidates.FirstOrDefault(w => w.IsActive) ?? (candidates.Count == 1 ? candidates[0] : null);
    }

    public static Scene? FromCommandLine()
    {
        if (!CommandLineEncoder.TryParseArguments(Environment.GetCommandLineArgs(), out var input, out _))
            return null;
        input = Path.GetFullPath(input);
        Log.Write("SceneLocator: loading " + input);
        // The host may be awaiting on the UI thread; run on a thread without a SynchronizationContext so blocking cannot deadlock.
        return Task.Run(async () =>
        {
            var project = YmmJson.Load<YmmProject>(input);
            project.LoadOldTimeline();
            project.ReplaceFilePath(input);
            project.Migrate(SettingsBase<CharacterSettings>.Default.CharacterLoadMode);
            var scenes = await CommandLineEncoder.PrepareScenesAsync(project).ConfigureAwait(false);
            var index = Math.Clamp(project.SelectedTimelineIndex, 0, project.Timelines.Count - 1);
            var timeline = project.Timelines[index];
            Log.Write($"SceneLocator: loaded. timeline length={timeline.Length} items={timeline.Items.Count}");
            return new Scene(timeline, scenes, Array.Empty<Guid>());
        }).GetAwaiter().GetResult();
    }

    public static Scene? FromGui()
    {
        var app = Application.Current;
        if (app == null) return null;
        Scene? Find()
        {
            // Several main windows can be open, so pick the one that started the export:
            // 1) the one whose Timeline matches the (project name, VideoInfo, length) seen by the export dialog,
            // 2) the active one, 3) the only candidate. If none is identifiable return null (sequential fallback) rather than risk the wrong project.
            var candidates = app.Windows.Cast<Window>().Where(w => w.DataContext is MainViewModel).ToList();
            var chosen = Choose(candidates);
            Log.Write($"SceneLocator: gui candidates={candidates.Count} active={candidates.Count(w => w.IsActive)} chosen={(chosen is null ? "none" : "yes")}");
            if (chosen is null) return null;
            var model = ((MainViewModel)chosen.DataContext).model;
            return new Scene(model.Timeline, model.Scenes, Array.Empty<Guid>());
        }
        if (app.Dispatcher.CheckAccess()) return Find();
        // Do not hang here if the UI thread is stuck.
        Scene? result = null;
        var op = app.Dispatcher.BeginInvoke(new Action(() => result = Find()), DispatcherPriority.Send);
        if (op.Wait(TimeSpan.FromSeconds(10)) != DispatcherOperationStatus.Completed)
        {
            op.Abort();
            Log.Write("SceneLocator: UI dispatcher timeout");
            return null;
        }
        return result;
    }
}
