using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ParallelRender;

/// <summary>
/// Samples this process's dedicated GPU memory (the Task Manager value) once a second from the Windows performance
/// counter \GPU Process Memory(pid_N_*)\Dedicated Usage. DXGI's CurrentUsage double-counts shared slots opened on
/// worker devices and saturates near the budget, so it cannot drive the VRAM guard or FastPsdTachie's cache shrink.
/// When the counter is unavailable <see cref="UsedBytes"/> is null and callers fall back to the DXGI estimate.
/// </summary>
internal static class GpuMemoryProbe
{
    private const uint PDH_FMT_LARGE = 0x400;
    private const uint PDH_MORE_DATA = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? src, nint user, out nint q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(nint q, string path, nint user, out nint c);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint q);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(nint c, uint fmt, ref uint bufSize, out uint count, nint buf);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint q);

    [StructLayout(LayoutKind.Sequential)] private struct Item { public nint Name; public uint CStatus; public long Value; }

    private static readonly object Gate = new();
    private static Thread? _thread;
    private static long _used = -1;          // last sampled value in bytes, -1 if none
    private static long _stamp;              // its timestamp (Stopwatch)
    private static long _createdAtSample;    // Vram.SlotBytesCreated at sample time

    /// <summary>Last sampled usage plus slots created since; null if there is no sample from the last 3 s.</summary>
    public static ulong? UsedBytes
    {
        get
        {
            long u = Volatile.Read(ref _used);
            if (u < 0 || (Stopwatch.GetTimestamp() - Volatile.Read(ref _stamp)) > 3 * Stopwatch.Frequency) return null;
            long since = Math.Max(0, Vram.SlotBytesCreated - Volatile.Read(ref _createdAtSample));
            return (ulong)(u + since);
        }
    }

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "gpu-mem-probe" };
            _thread.Start();
        }
    }

    private static void Loop()
    {
        nint q = 0;
        try
        {
            if (PdhOpenQuery(null, 0, out q) != 0) return;
            if (PdhAddEnglishCounterW(q, $"\\GPU Process Memory(pid_{Environment.ProcessId}_*)\\Dedicated Usage", 0, out var c) != 0) return;
            while (true)   // runs for the life of the process
            {
                long created = Vram.SlotBytesCreated;
                if (PdhCollectQueryData(q) == 0 && TryRead(c, out long total))
                {
                    Volatile.Write(ref _createdAtSample, created);
                    Volatile.Write(ref _used, total);
                    Volatile.Write(ref _stamp, Stopwatch.GetTimestamp());
                }
                Thread.Sleep(1000);
            }
        }
        catch (Exception ex) { Log.Write("GpuMemoryProbe: " + ex.Message); }
        finally { if (q != 0) PdhCloseQuery(q); Volatile.Write(ref _used, -1); _thread = null; }
    }

    private static bool TryRead(nint counter, out long total)
    {
        total = 0;
        uint size = 0;
        uint r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out uint count, 0);
        if (r != PDH_MORE_DATA || size == 0) return false;
        nint buf = Marshal.AllocHGlobal((int)size);
        try
        {
            r = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref size, out count, buf);
            if (r != 0) return false;
            int sz = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var it = Marshal.PtrToStructure<Item>(buf + i * sz);
                if (it.CStatus == 0 || it.CStatus == 1) total += it.Value;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
            }
            return count > 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
