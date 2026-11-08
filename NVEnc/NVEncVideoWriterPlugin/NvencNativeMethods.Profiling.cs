using System.Runtime.InteropServices;

namespace NVEncVideoWriterPlugin;

// Profiling half of the P/Invoke surface. Kept apart from NvencNativeMethods.cs so that ParallelRender can compile
// that file alone (see ParallelRenderPlugin.csproj) without pulling in TimelineRecorder's NativeTotals.
internal static partial class NvencNativeMethods
{
    [DllImport("NvencNative.dll")]
    public static extern int NvencEnableProfiling(IntPtr handle);

    [DllImport("NvencNative.dll")]
    public static extern int NvencGetProfile(IntPtr handle, out NativeProfileSnapshot result, uint resultSize);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProfileMetric
{
    public ulong Count;
    public ulong TotalNanoseconds;
    public ulong MaxNanoseconds;
}

// Must match NvencProfileSnapshot in NvencNative.h (24 + 12 * 24 = 312 bytes).
[StructLayout(LayoutKind.Sequential)]
internal struct NativeProfileSnapshot
{
    public uint Version;
    public uint MetricCount;
    public ulong SubmittedFrames;
    public ulong CompletedFrames;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
    public NativeProfileMetric[] Metrics;

    internal static readonly string[] StageNames =
    [
        "slot_wait", "copy_resource_cpu", "map_input", "encode_picture", "output_event_wait",
        "lock_bitstream", "bitstream_mux", "audio_write", "writer_sample",
        "finalize", "mp4_finalize", "slot_residence",
    ];

    // Cumulative counters used by the timeline (slot_wait, encode_picture, output_event_wait, lock_bitstream, bitstream_mux).
    public NativeTotals ToTimelineTotals()
    {
        if (Version != 1 || MetricCount != StageNames.Length || Metrics is null || Metrics.Length != StageNames.Length)
            throw new InvalidOperationException("NVENCプロファイリングのDLLバージョンが一致しません。");
        return new NativeTotals(Metrics[0].TotalNanoseconds, Metrics[3].TotalNanoseconds, Metrics[4].TotalNanoseconds,
            Metrics[5].TotalNanoseconds, Metrics[6].TotalNanoseconds);
    }

    public object ToReport()
    {
        if (Version != 1 || MetricCount != StageNames.Length || Metrics is null || Metrics.Length != StageNames.Length)
            throw new InvalidOperationException("NVENCプロファイリングのDLLバージョンが一致しません。");
        var stages = new Dictionary<string, object>();
        for (var i = 0; i < StageNames.Length; i++)
        {
            var metric = Metrics[i];
            stages.Add(StageNames[i], new
            {
                count = metric.Count,
                total_ms = metric.TotalNanoseconds / 1_000_000d,
                mean_ms = metric.Count == 0 ? 0 : metric.TotalNanoseconds / 1_000_000d / metric.Count,
                max_ms = metric.MaxNanoseconds / 1_000_000d,
            });
        }
        return new { version = Version, submitted_frames = SubmittedFrames, completed_frames = CompletedFrames, stages };
    }
}
