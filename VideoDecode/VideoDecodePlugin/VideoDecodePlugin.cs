using System;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileSource;
using YukkuriMovieMaker.Plugin.Update;

namespace VideoDecode;

/// <summary>
/// Video source plugin that skips decoding of frames the player would discard (non-reference frame skipping).
/// It takes only 8-bit SDR progressive unrotated 4:2:0 H.264/HEVC in mp4/mkv; for anything else or on open failure it
/// returns null so the next plugin (standard FFmpeg) handles the file.
/// </summary>
public sealed class SkipVideoFileSourcePlugin : IVideoFileSourcePlugin
{
    static SkipVideoFileSourcePlugin() => ChunkHints.RegisterProbe();

    public string Name => "VideoDecode (非参照フレームのスキップ)";

    public PluginDetailsAttribute Details => new()
    {
        AuthorName = "omi",
        ContentId = "VideoDecodeSkip",
    };

    public IPluginUpdater? Updater => null;

    public IVideoFileSource? CreateVideoFileSource(IGraphicsDevicesAndContext devices, string filePath)
    {
        try
        {
            return SkipVideoFileSource.TryCreate(devices, filePath);
        }
        catch (Exception ex)
        {
            Log.Default.Write("VideoDecode: CreateVideoFileSource exception: " + ex.Message, ex);
            return null;
        }
    }
}

/// <summary>
/// Decides whether to decode and convert on the render device (SameDevProcessor): only on ParallelRender's worker devices,
/// which it registers in AppDomain data "ParallelRender.WorkerD3DDevices" (ConcurrentDictionary&lt;nint, bool&gt;, keyed by
/// device pointer). Those devices are never used by the host preview or the standard export.
/// </summary>
internal static class SameDevPolicy
{
    private const string RegistryKey = "ParallelRender.WorkerD3DDevices";

    private static int loggedOn, loggedOff;

    public static bool For(IGraphicsDevicesAndContext devices)
    {
        string why;
        bool on;
        try
        {
            on = AppDomain.CurrentDomain.GetData(RegistryKey) is System.Collections.Concurrent.ConcurrentDictionary<nint, bool> r
                && r.ContainsKey(devices.D3D.Device.NativePointer);
            why = on ? "ParallelRender worker device" : "not a ParallelRender worker device";
        }
        catch { on = false; why = "registry lookup failed"; }
        if (on ? System.Threading.Interlocked.Exchange(ref loggedOn, 1) == 0 : System.Threading.Interlocked.Exchange(ref loggedOff, 1) == 0)
            Log.Default.Write($"VideoDecode: same-device decode {(on ? "ON" : "OFF")} ({why})");
        return on;
    }
}
