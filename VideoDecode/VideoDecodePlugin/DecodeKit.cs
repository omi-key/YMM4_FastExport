using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.FileSource.FFmpeg;
using FFmpeg.AutoGen;

namespace VideoDecode;

/// <summary>
/// A decode-only D3D device and context. It is independent of the render device, so a disposed source's kit is pooled and
/// reused (creation costs tens of ms per source). The cross-device bridge and VideoProcessor bind to the render-side context
/// and stay per source. A kit serves one source at a time; kits idle for a while are disposed.
/// </summary>
internal sealed class DecodeKit : IDisposable
{
    public readonly GraphicsDevices Devices;
    public readonly IGraphicsDevicesAndContext Context;
    public long LastUsedTicks = Stopwatch.GetTimestamp();
    public bool Broken;

    private DecodeKit()
    {
        Devices = new GraphicsDevices();
        Context = Devices.CreateContext();
    }

    public static DecodeKit Create() => new();

    public void Dispose()
    {
        try { Context.Dispose(); } catch { }
        try { Devices.Dispose(); } catch { }
    }
}

internal static class DecodeKitPool
{
    private const double IdleSeconds = 5.0;
    private static readonly long IdleTicks = (long)(IdleSeconds * Stopwatch.Frequency);
    private static readonly object gate = new();
    private static readonly Stack<DecodeKit> pool = new();
    private static Timer? reaper;

    public static DecodeKit Rent()
    {
        lock (gate)
        {
            if (pool.Count > 0) return pool.Pop();
        }
        return DecodeKit.Create();
    }

    public static void Return(DecodeKit kit)
    {
        if (kit.Broken) { kit.Dispose(); return; }
        kit.LastUsedTicks = Stopwatch.GetTimestamp();
        lock (gate)
        {
            pool.Push(kit);
            reaper ??= new Timer(_ => Reap(), null, 1000, 1000);
        }
    }

    private static void Reap()
    {
        List<DecodeKit>? dead = null;
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            var keep = new List<DecodeKit>();
            while (pool.Count > 0)
            {
                var k = pool.Pop();
                if (now - k.LastUsedTicks > IdleTicks) (dead ??= new()).Add(k); else keep.Add(k);
            }
            for (int i = keep.Count - 1; i >= 0; i--) pool.Push(keep[i]);
        }
        if (dead != null) foreach (var k in dead) k.Dispose();
    }
}

/// <summary>
/// Pools the AVFormatContext of disposed sources per file, so reopening the same file skips avformat_open_input and
/// avformat_find_stream_info (which re-parse the container index, slow for long files). One source uses a context at a time,
/// and the next source always positions it with its first seek. A context is not reused if the file size or mtime changed.
/// </summary>
internal static unsafe class FormatPool
{
    private sealed class Entry { public nint Fmt; public string Sig = ""; public long LastUsed; }
    private const double IdleSeconds = 10.0;
    private const int MaxPerPath = 4;   // cap on idle contexts kept per file
    private static readonly long IdleTicks = (long)(IdleSeconds * Stopwatch.Frequency);
    private static readonly object gate = new();
    private static readonly Dictionary<string, Stack<Entry>> pool = new(StringComparer.OrdinalIgnoreCase);
    private static Timer? reaper;

    public static string Signature(string path)
    {
        try { var fi = new System.IO.FileInfo(path); return fi.Length + ":" + fi.LastWriteTimeUtc.Ticks; } catch { return ""; }
    }

    /// <summary>Borrows a pooled context whose signature matches; 0 if none.</summary>
    public static nint Rent(string path, string sig)
    {
        if (sig.Length == 0) return 0;
        lock (gate)
        {
            if (pool.TryGetValue(path, out var st))
                while (st.Count > 0)
                {
                    var e = st.Pop();
                    if (e.Sig == sig) return e.Fmt;
                    CloseNow(e.Fmt);
                }
        }
        return 0;
    }

    /// <summary>Returns a context; true if pooled (the caller must not close it).</summary>
    public static bool Return(string path, string sig, nint fmt)
    {
        if (sig.Length == 0) return false;
        lock (gate)
        {
            if (!pool.TryGetValue(path, out var st)) pool[path] = st = new Stack<Entry>();
            if (st.Count >= MaxPerPath) return false;
            st.Push(new Entry { Fmt = fmt, Sig = sig, LastUsed = Stopwatch.GetTimestamp() });
            reaper ??= new Timer(_ => Reap(), null, 2000, 2000);
        }
        return true;
    }

    private static void CloseNow(nint fmt)
    {
        AVFormatContext* x = (AVFormatContext*)fmt;
        FFmpeg.AutoGen.ffmpeg.avformat_close_input(&x);
    }

    private static void Reap()
    {
        List<nint>? dead = null;
        long now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            List<string>? empty = null;
            foreach (var (path, st) in pool)
            {
                var keep = new List<Entry>();
                while (st.Count > 0)
                {
                    var e = st.Pop();
                    if (now - e.LastUsed > IdleTicks) (dead ??= new()).Add(e.Fmt); else keep.Add(e);
                }
                for (int i = keep.Count - 1; i >= 0; i--) st.Push(keep[i]);
                if (st.Count == 0) (empty ??= new()).Add(path);
            }
            if (empty != null) foreach (var p in empty) pool.Remove(p);
        }
        if (dead != null) foreach (var f in dead) CloseNow(f);
    }
}
