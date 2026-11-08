using System.IO;
using System.Diagnostics;

namespace ParallelRender;

/// <summary>Plain file log. PR_LOG=&lt;path&gt; (default %TEMP%\pr_log.txt).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static readonly string Path = Environment.GetEnvironmentVariable("PR_LOG")
        ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pr_log.txt");

    public static void Write(string message)
    {
        var line = $"[{Clock.Elapsed.TotalSeconds,8:F2}s T{Environment.CurrentManagedThreadId,-3}] {message}";
        lock (Gate)
        {
            try { File.AppendAllText(Path, line + Environment.NewLine); } catch { }
        }
    }
}
