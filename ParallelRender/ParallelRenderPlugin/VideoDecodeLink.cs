using System.Collections.Concurrent;

namespace ParallelRender;

/// <summary>
/// Link to VideoDecode (the video-reader plugin, a separate DLL). The link is by name through AppDomain data only, with no project reference:
///  - "ParallelRender.WorkerD3DDevices" (ConcurrentDictionary&lt;nint, bool&gt;: device pointer -> true): D3D11 devices created for
///    workers. When VideoDecode creates a source on a registered device it decodes directly. The host's preview and stock export
///    devices are never registered, so they are unaffected.
///  - "VideoDecode.KeyframeProbe": published by VideoDecode; path -> [frame ticks, duration ticks, keyframe times...].
/// Do not rename these keys or change their value types.
/// </summary>
internal static class VideoDecodeLink
{
    private const string Key = "ParallelRender.WorkerD3DDevices";
    private static readonly object Gate = new();

    private static ConcurrentDictionary<nint, bool> Table()
    {
        lock (Gate)
        {
            if (AppDomain.CurrentDomain.GetData(Key) is ConcurrentDictionary<nint, bool> t) return t;
            t = new ConcurrentDictionary<nint, bool>();
            AppDomain.CurrentDomain.SetData(Key, t);
            return t;
        }
    }

    public static void Register(nint d3dDevice) { if (d3dDevice != 0) Table()[d3dDevice] = true; }

    public static void Unregister(nint d3dDevice) { if (d3dDevice != 0) Table().TryRemove(d3dDevice, out _); }

    private const string ProbeKey = "VideoDecode.KeyframeProbe";

    /// <summary>Registered by VideoDecode: path -> [frame length, source duration, keyframe times...] in 100 ns units; null for sources it does not handle.</summary>
    public static Func<string, long[]?>? KeyframeProbe => AppDomain.CurrentDomain.GetData(ProbeKey) as Func<string, long[]?>;
}
