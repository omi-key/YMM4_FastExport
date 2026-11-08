using System.Diagnostics;
using Vortice.Direct2D1;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.VideoFileWriter;
using YukkuriMovieMaker.Project;
using ParallelRender.Encoding;

namespace ParallelRender;

internal static class WriterCommon
{
    /// <summary>Same range rule as the host's own export (EncodeFrom/EncodeTo may be given in either order).</summary>
    public static (int start, int length) EncodeRange(Scene scene)
    {
        var st = SettingsBase<VideoFileWriterSettings>.Default;
        int len = scene.Timeline.Length;
        int from = Math.Max(0, Math.Min(Math.Min(st.EncodeFrom, st.EncodeTo), len - 1));
        int to = Math.Min(len, Math.Max(st.EncodeFrom, st.EncodeTo));
        return (from, to - from);
    }
}

/// <summary>
/// Parallel export. SupportedStreams is Audio only, so the host never draws video and just calls WriteAudio serially;
/// this class draws video itself through FramePipeline and feeds EncoderCore in order.
/// The native muxer writes samples to mdat in arrival order, so audio and video may lead each other by at most
/// <see cref="Lead"/> frames to keep the interleave tight.
/// </summary>
internal sealed class ParallelWriter : IVideoFileWriter3
{
    private const int Lead = 120;
    // Rough non-frame VRAM use (tachie cache, decoders, per-worker devices) when sizing the GPU-direct window.
    private const long BaseVramBytes = 4608L * 1024 * 1024;
    // GPU-direct shared textures get at least this share of the DXGI budget (FastPsdTachie's cache takes 20%).
    private const double GpuBudgetShare = 0.25;
    // CPU memory ceiling up to which the adaptive window may grow.
    private const long AdaptMaxBytes = 12L * 1024 * 1024 * 1024;
    // The host reads all audio before its first WriteAudio, so video is not paced until audio catches up.
    private volatile bool _audioCaughtUp;
    private readonly EncoderCore _core;
    private readonly FramePipeline _pipe;
    private readonly int _count;
    private readonly object _gate = new();   // audio/video pacing
    private int _audioWritten, _videoSubmitted;
    private int _encThreads = 1, _encChunk;   // decided by EncoderCore
    private bool _disposed;

    public ParallelWriter(string path, VideoInfo vi, ParallelSettings s, Scene scene)
    {
        var (start, length) = WriterCommon.EncodeRange(scene);
        _count = length;
        if (_count <= 0) throw new InvalidOperationException("出力範囲が空です。");
        int p = Math.Min(s.EffectiveParallelism, Math.Max(1, _count));
        long frameBytes = (long)vi.Width * vi.Height * 4;
        bool wantGpu = s.GpuDirect != GpuDirectMode.Off;
        int memCap = (int)Math.Clamp(s.BufferMb * 1024L * 1024L / frameBytes, 2 * p, 1 << 16);   // RAM limit of the CPU-readback buffer
        int winCap = wantGpu ? 1 << 16 : memCap;   // the GPU-direct window is bounded by the VRAM budget below, not RAM
        // Window = frames held by the reorder buffer (P x chunk by default).
        int window = Math.Min(winCap, Math.Max(2 * p, p * s.ChunkFrames));
        int startWindow = window;   // adaptive start value
        SharedFramePool? gpu = null;
        long gpuBudget = 0;
        string path_ = "cpu-readback";
        string gpuNote = wantGpu ? "" : $"disabled ({s.GpuDirect})";
        // Adaptive ceiling: up to 4x the start window, further limited by the memory/VRAM budget.
        int adaptMax = Math.Max(window, (int)Math.Min(4L * startWindow, Math.Max(2 * p, AdaptMaxBytes / frameBytes)));
        try
        {
            _core = new EncoderCore(path, vi, s);
            // With several sessions each encode thread must see the first frame of its own chunk, so the window
            // needs at least (sessions + 1) chunks.
            if (_core is { Sessions: > 1 } mc)
            {
                _encThreads = mc.Sessions; _encChunk = mc.EncChunk;
                int need = (_encThreads + 1) * _encChunk;
                window = Math.Min(winCap, Math.Max(window, need));
                startWindow = Math.Min(window, Math.Max(startWindow, need));
                adaptMax = Math.Max(adaptMax, window);
            }
            if (wantGpu)
            {
                try
                {
                    gpu = new SharedFramePool(_core.Device!, vi.Width, vi.Height, p);
                    var vm = Vram.Query();
                    // Budget = max(GpuBudgetShare of the DXGI budget, budget x guard - base usage). The base usage
                    // is large on big cards, so small cards fall back to the plain share.
                    long budgetBytes = vm is { } v ? Math.Max((long)(v.budget * GpuBudgetShare), (long)(v.budget * SharedFramePool.VramGuard) - BaseVramBytes) : 2L * 1024 * 1024 * 1024;
                    gpuBudget = budgetBytes;
                    int gpuFrames = (int)Math.Max(2 * p, budgetBytes / frameBytes);
                    // In Auto mode a budget below half the default window would shrink the window and run slower than CPU readback.
                    if (s.GpuDirect == GpuDirectMode.Auto && gpuFrames < p * 60)
                        throw new InvalidOperationException($"VRAM budget {budgetBytes / (1024 * 1024)} MB is below P x 60 = {p * 60} frames; not using GPU direct (set 'GPU 直渡し' to ON in the settings to force it)");
                    if (gpuFrames < window) { window = gpuFrames; startWindow = Math.Min(startWindow, gpuFrames); }
                    adaptMax = Math.Min(adaptMax, Math.Max(gpuFrames, window));
                    path_ = "gpu-direct";
                    gpuNote = $"budget {budgetBytes / (1024 * 1024)} MB = {gpuFrames} frames" + (vm is { } vv ? $", VRAM {vv.usage / (1024 * 1024)}/{vv.budget / (1024 * 1024)} MB" : "");
                }
                catch (Exception ex)
                {
                    gpu?.Dispose(); gpu = null;
                    window = Math.Min(window, memCap); startWindow = Math.Min(startWindow, window); adaptMax = Math.Min(adaptMax, memCap);   // back to the RAM limit
                    gpuNote = "unavailable: " + ex.Message;
                    Log.Write("GPU direct unavailable -> CPU readback: " + ex);
                }
            }
            int startW = s.AdaptiveWindow ? Math.Min(startWindow, window) : window;
            int maxWindow = s.AdaptiveWindow ? Math.Max(startW, adaptMax) : startW;
            int chunk = Math.Max(1, Math.Min(s.ChunkFrames, startW / p));
            Log.Write($"ParallelWriter: frames {start}+{_count} P={p} chunk={chunk} window={startW}..{maxWindow} adaptive={s.AdaptiveWindow} ({startW * frameBytes / (1024 * 1024)} MB) path={path_} [{gpuNote}] sessions={_encThreads} enc-chunk={_encChunk}");
            gpu?.SetCap(maxWindow + p);   // slots in use stay within the window (+1 per worker being drawn)
            var cfg = new PipeConfig { Window = startW, MaxWindow = maxWindow, Adaptive = s.AdaptiveWindow, Gpu = gpu, GpuBudgetBytes = gpuBudget, EncThreads = _encThreads, EncChunk = _encChunk, TextCache = s.TextCache };
            _pipe = new FramePipeline(scene, start, _count, p, chunk, cfg, new Sink(this), () => { lock (_gate) Monitor.PulseAll(_gate); },
                () => { lock (_gate) { _videoSubmitted = Math.Max(_videoSubmitted, _pipe.Encoded); Monitor.PulseAll(_gate); } });
            _pipe.Start();
        }
        catch { _core?.Abort(); gpu?.Dispose(); throw; }
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Audio;
    public bool IsGpuFrameSupported => false;
    public void WriteVideo(byte[] frame) { }
    public void WriteVideo(ID2D1Bitmap1 frame) { }

    private sealed class Sink(ParallelWriter w) : IFrameSink
    {
        private bool Pace(int index)
        {
            lock (w._gate)
            {
                while (!w._pipe.Stopped && w._audioCaughtUp && index - w._audioWritten > Lead + (w._encThreads - 1) * w._encChunk) Monitor.Wait(w._gate);
            }
            return !w._pipe.Stopped;
        }

        public void Submit(int index, byte[] bgra)
        {
            if (!Pace(index)) return;
            w._core.Submit(bgra, index);
        }

        public void SubmitShared(int index, SharedSlot slot)
        {
            if (!Pace(index)) return;
            w._core.SubmitShared(slot, index);
        }
    }

    public void WriteAudio(float[] samples)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ParallelWriter));
        ThrowIfFailed();
        int idx;
        lock (_gate)
        {
            idx = _audioWritten;
            while (!_pipe.Stopped && idx - _videoSubmitted > Lead) Monitor.Wait(_gate);
        }
        ThrowIfFailed();
        if (_pipe.Stopped) return; // cancelled
        try { _core.WriteAudio(samples); }
        catch (Exception ex) { _pipe.Fail(ex); throw; }
        lock (_gate)
        {
            _audioWritten = idx + 1;
            if (!_audioCaughtUp && _audioWritten + Lead >= _videoSubmitted) _audioCaughtUp = true;
            Monitor.PulseAll(_gate);
        }
    }

    private void ThrowIfFailed()
    {
        var e = _pipe.Error;
        if (e != null) throw new InvalidOperationException("並列出力が失敗しました: " + e.Message, e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Exception? failure = null;
        bool complete = false;
        try
        {
            // Normal completion only if the host delivered audio for every frame; otherwise it was cancelled.
            if (_audioWritten >= _count)
            {
                complete = _pipe.WaitCompletion();
                if (complete) _core.Finish();
            }
            else Log.Write($"ParallelWriter: aborted before the host finished audio (audio {_audioWritten}/{_count}, encoded {_pipe.Encoded}, error={_pipe.Error?.Message ?? "none"})");
        }
        catch (Exception ex) { failure = ex; _pipe.Fail(ex); }
        finally
        {
            _pipe.Stop();
            _pipe.Dispose();
            failure ??= _pipe.Error;
            if (!complete || failure != null) _core.Abort(); else _core.Dispose();
        }
        if (failure != null) throw new InvalidOperationException("並列出力が失敗しました (出力ファイルは削除しました): " + failure.Message, failure);
    }
}

/// <summary>
/// Sequential fallback (disabled, or no Scene available): the host draws each frame (CPU-readback bitmap)
/// and the same EncoderCore feeds NVENC. No parallel rendering.
/// </summary>
internal sealed class SequentialWriter : IVideoFileWriter3
{
    private readonly EncoderCore _core;
    private byte[] _buf;
    private int _index;
    private bool _disposed, _failed;

    public SequentialWriter(string path, VideoInfo vi, ParallelSettings s)
    {
        _buf = new byte[vi.Width * vi.Height * 4];
        var one = s.Clone();
        one.EncSessions = 1;   // frames arrive one at a time from the host, so there is nothing to spread over sessions
        _core = new EncoderCore(path, vi, one);
    }

    public VideoFileWriterSupportedStreams SupportedStreams => VideoFileWriterSupportedStreams.Audio | VideoFileWriterSupportedStreams.Video;
    public bool IsGpuFrameSupported => false;
    public void WriteVideo(byte[] frame) { }

    public void WriteVideo(ID2D1Bitmap1 frame)
    {
        try
        {
            frame.ToBytes(ref _buf);
            _core.Submit(_buf, _index++);
        }
        catch { _failed = true; throw; }
    }

    public void WriteAudio(float[] samples)
    {
        try { _core.WriteAudio(samples); } catch { _failed = true; throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_failed) _core.Finish();
        }
        catch { _failed = true; throw; }
        finally
        {
            if (_failed) _core.Abort(); else _core.Dispose();
        }
    }
}
