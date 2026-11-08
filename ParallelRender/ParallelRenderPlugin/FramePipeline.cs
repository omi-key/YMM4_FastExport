using System.Diagnostics;
using YukkuriMovieMaker.Project;

namespace ParallelRender;

/// <summary>Receiver of video frames, called in order (by one encode thread per session).</summary>
internal interface IFrameSink
{
    /// <summary>Takes frame index (0-based). bgra is valid only during the call. May block (back-pressure).</summary>
    void Submit(int index, byte[] bgra);
    /// <summary>GPU direct: takes a drawn shared texture (encoder-device side). The slot returns to the pool after this returns.</summary>
    void SubmitShared(int index, SharedSlot slot);
}

/// <summary>Window and GPU-direct configuration.</summary>
internal sealed class PipeConfig
{
    /// <summary>Initial window (frames).</summary>
    public int Window;
    /// <summary>Window ceiling (how far adaptation may grow it; equals Window when fixed). Size of the ring buffer.</summary>
    public int MaxWindow;
    public bool Adaptive;
    /// <summary>GPU-direct pool (null = CPU readback). Owned by the caller's setup; the pipeline disposes it.</summary>
    public SharedFramePool? Gpu;
    /// <summary>VRAM budget (bytes) within which the window may grow on the GPU-direct path.</summary>
    public long GpuBudgetBytes;
    /// <summary>Encode threads (= encode sessions). With 2 or more, encode chunk c (EncChunk frames) is handled by thread c % EncThreads.</summary>
    public int EncThreads = 1;
    /// <summary>Encode chunk length in frames; used only when EncThreads >= 2.</summary>
    public int EncChunk;
    /// <summary>Subtitle effect cache on the workers (<see cref="TextCache"/>).</summary>
    public bool TextCache = true;
}

/// <summary>
/// Parallel render pipeline.
/// P workers (each with its own Renderer = dedicated GraphicsDevices + TimelineSource) dynamically take contiguous
/// chunks, draw them, and place the frames in a windowed reorder buffer. The encode thread(s) hand frames to the sink in index order.
/// With EncThreads >= 2, thread t owns encode chunks c with c % EncThreads == t and feeds each chunk in order;
/// the sink's muxer restores order across chunks. next = the minimum of the threads' next frames.
/// Window invariant: a worker starts frame f only while f &lt; next + window, so at most `window` buffers are ever in use
/// (the buffer pool bound), and the worker holding frame `next` is always inside the window, so there is no deadlock.
/// The first exception stops everything (Error); Stop() cancels without an exception.
/// </summary>
internal sealed class FramePipeline : IDisposable
{
    private readonly Scene _scene;
    private readonly int _from, _count, _workers, _chunk;
    private volatile int _window;            // current window; adapts, never above _ring.Length
    private readonly PipeConfig _cfg;
    private readonly SharedFramePool? _gpu;
    private readonly IFrameSink _sink;
    private readonly object _gate = new();
    private readonly object?[] _ring;       // byte[] (CPU path) or SharedSlot (GPU direct)
    private readonly Stack<byte[]> _pool = new();
    private int _rendered;          // frames placed in the buffer (cumulative)
    private int _next;              // next frame to encode (0-based)
    private readonly object _dispGate = new();
    private int _dispNext;          // first frame of the next chunk to hand out

    private volatile int[]? _plan; private int _planIdx;   // ChunkPlanner's boundary array; _planIdx guarded by _dispGate

    /// <summary>
    /// The plan is built on its own thread so the workers can start at once. Chunks handed out before it is ready use the
    /// nominal length; plan boundaries are absolute, so switching midway stays consistent.
    /// </summary>
    private void BuildPlan()
    {
        new Thread(() =>
        {
            try
            {
                var plan = ChunkPlanner.Build(_scene, _from, _count, _chunk, out var info);
                Log.Write("chunk plan: " + (plan is null ? "off (" + info + ")" : info));
                _plan = plan;
            }
            catch (Exception ex) { Log.Write("chunk plan failed: " + ex.Message); }
        }) { IsBackground = true, Name = "chunk-plan" }.Start();
    }

    /// <summary>End of the chunk starting at a: from the plan when ready, otherwise a + chunk.</summary>
    private int NextBoundary(int a)
    {
        if (_plan is { } plan)
        {
            while (_planIdx < plan.Length - 1 && plan[_planIdx] <= a) _planIdx++;
            return plan[_planIdx];
        }
        return (int)Math.Min((long)a + _chunk, _count);
    }
    private volatile bool _stop;    // failed or cancelled
    private Exception? _error;
    private readonly Thread[] _threads;
    private readonly Thread[] _encodeThreads;
    private readonly int _encThreads, _encChunk;
    private readonly int[] _cursors;       // per encode thread: next frame to process (guarded by _gate); _next = their minimum
    private readonly Action _onProgress;
    private readonly Action _onStop;
    private int _activeWorkers;
    public int Workers => _workers;
    public int Window => _window;
    public bool GpuDirect => _gpu is not null;

    private long _workerWaitTicks;   // total time workers waited on a full window (drives the adaptive window)

    public Exception? Error => Volatile.Read(ref _error);
    public bool Stopped => _stop;
    public int Encoded => Volatile.Read(ref _next);

    public FramePipeline(Scene scene, int from, int count, int workers, int chunk, PipeConfig cfg, IFrameSink sink, Action onStop, Action onProgress)
    {
        _scene = scene; _from = from; _count = count; _workers = Math.Max(1, workers);
        _chunk = Math.Max(1, chunk); _cfg = cfg; _gpu = cfg.Gpu;
        cfg.MaxWindow = Math.Max(Math.Max(_workers, cfg.Window), cfg.MaxWindow);
        _window = Math.Max(_workers, cfg.Window); _sink = sink; _onStop = onStop;
        _ring = new object?[cfg.MaxWindow];
        _threads = new Thread[_workers];
        _encThreads = Math.Max(1, cfg.EncThreads); _encChunk = Math.Max(1, cfg.EncChunk); _onProgress = onProgress;
        _cursors = new int[_encThreads];
        for (int t = 0; t < _encThreads; t++) _cursors[t] = (int)Math.Min((long)t * _encChunk, _count);
        _encodeThreads = new Thread[_encThreads];
        for (int t = 0; t < _encThreads; t++)
        {
            int ti = t;
            _encodeThreads[t] = new Thread(() => EncodeLoop(ti), 4 * 1024 * 1024) { Name = _encThreads == 1 ? "pr-encode" : $"pr-encode-{t}", IsBackground = true };
        }
    }

    public void Start()
    {
        BuildPlan();
        if (_workers >= 2) TachieLink.SetMultiDevice(true);

        _activeWorkers = _workers;
        for (int w = 0; w < _workers; w++)
        {
            int wi = w;
            _threads[w] = new Thread(() => WorkerLoop(wi), 16 * 1024 * 1024) { Name = $"pr-worker-{w}", IsBackground = true };
            _threads[w].Start();
        }
        foreach (var t in _encodeThreads) t.Start();
    }

    /// <summary>Records only the first error and stops everything.</summary>
    public void Fail(Exception ex)
    {
        Interlocked.CompareExchange(ref _error, ex, null);
        Stop();
    }

    /// <summary>Stops without an exception (cancel).</summary>
    public void Stop()
    {
        _stop = true;
        lock (_gate) Monitor.PulseAll(_gate);
        try { _onStop(); } catch { }
    }

    /// <summary>Waits until every frame has been encoded (sink calls done). False on failure or cancel.</summary>
    public bool WaitCompletion()
    {
        foreach (var t in _encodeThreads) t.Join();
        foreach (var t in _threads) t?.Join();
        TachieLink.SetMultiDevice(false);
        return !_stop && Encoded == _count;
    }

    private byte[] Rent(int bytes)
    {
        lock (_gate)
        {
            if (_pool.Count > 0) return _pool.Pop();
        }
        return new byte[bytes];
    }

    private void WorkerLoop(int wi)
    {
        Renderer? r = null;
        var sw = new Stopwatch();
        try
        {
            try { r = new Renderer(_scene, wi, cpuPath: _gpu is null, textCache: _cfg.TextCache); }
            catch (Exception ex)
            {
                if (wi == 0) throw;
                // Setup failure of any worker but 0 (e.g. out of VRAM): continue with fewer workers.
                Log.Write($"worker {wi} setup failed, continuing with fewer workers: {ex.Message}");
                return;
            }

            var vi = _scene.Timeline.VideoInfo;
            int bytes = vi.Width * vi.Height * 4;
            bool gpuOk = _gpu is not null;
            int openFailures = 0;
            if (gpuOk)
            {
                // Check up front that this worker's device can open a shared texture; if not, only this worker falls back to CPU readback.
                var probe = _gpu!.Rent(wi);
                try
                {
                    if (probe is null) throw new InvalidOperationException("共有テクスチャを作れない: " + (_gpu.LastError ?? "?"));
                    r.OpenSlot(probe);
                }
                catch (Exception ex)
                {
                    gpuOk = false;
                    Log.Write($"worker {wi}: GPU direct unavailable, using CPU readback: {ex.Message}");
                    r.EnsureCpuPath();
                }
                finally { if (probe is not null) _gpu.Return(probe); }
            }
            void Publish(int pf, object pb)
            {
                lock (_gate)
                {
                    _ring[pf % _ring.Length] = pb;
                    _rendered++;
                    if (pf == _next) Monitor.PulseAll(_gate);
                }
            }
            while (!_stop)
            {
                int a, b;
                lock (_dispGate)
                {
                    a = _dispNext;
                    if (a >= _count) break;
                    b = NextBoundary(a);
                    _dispNext = b;
                }
                for (int f = a; f < b; f++)
                {
                    sw.Restart();
                    lock (_gate)
                    {
                        while (!_stop && f >= _next + _window) Monitor.Wait(_gate);
                    }
                    if (_stop) return;
                    Interlocked.Add(ref _workerWaitTicks, sw.ElapsedTicks);
                    SharedSlot? slot = gpuOk ? _gpu!.Rent(wi) : null;
                    if (slot is not null)
                    {
                        try { r.OpenSlot(slot); }
                        catch (Exception ex)
                        {
                            // Cannot open on this device (e.g. out of VRAM): drop the slot and read this frame back via CPU; after repeated failures the worker stays on the CPU path.
                            _gpu.Discard(slot); slot = null;
                            if (++openFailures >= 3) gpuOk = false;
                            Log.Write($"worker {wi}: opening a shared slot failed ({openFailures}): {ex.Message}");
                        }
                    }
                    byte[]? buf = null;
                    if (slot is null)
                    {
                        if (_gpu is not null) r.EnsureCpuPath();   // no slot available: this one frame goes through CPU readback
                        buf = Rent(bytes);
                    }
                    r.UpdateStage(_from + f);
                    if (slot is not null) r.DrawStageShared(slot); else r.DrawStage();
                    if (slot is null) r.ReadbackInto(buf!);   // GPU direct has no readback: the drawn shared texture goes straight into the ring
                    Publish(f, slot is not null ? slot : buf!);
                }
            }
        }
        catch (Exception ex) { Fail(new InvalidOperationException($"描画ワーカー {wi} が失敗しました: {ex.Message}", ex)); }
        finally
        {
            try { r?.Dispose(); } catch (Exception ex) { Log.Write($"worker {wi} dispose: {ex.Message}"); }
            // If every worker is gone but frames remain, fail instead of waiting forever.
            if (Interlocked.Decrement(ref _activeWorkers) == 0 && !_stop)
            {
                lock (_gate) { if (_rendered < _count && !_stop) Fail(new InvalidOperationException("描画ワーカーが全て終了したが、未描画のフレームが残っている")); }
            }
        }
    }

    private void EncodeLoop(int t)
    {
        try
        {
            if (t == 0) _adaptLast = Stopwatch.GetTimestamp();
            // One thread: 0..count-1 in index order. Several: this thread takes chunks c = t, t+K, ...
            int step = _encThreads == 1 ? _count : _encChunk;
            for (long a = (long)t * step; a < _count; a += (long)_encThreads * step)
            {
                int lo = (int)a, hi = (int)Math.Min(a + step, _count);
                for (int i = lo; i < hi; i++)
                {
                    object item;
                    lock (_gate)
                    {
                        // If i is beyond the ring (one lap ahead), wait so we never take a different frame from the same slot (several threads, another chunk lagging).
                        while (!_stop && (i >= _next + _ring.Length || _ring[i % _ring.Length] is null)) Monitor.Wait(_gate);
                        if (_stop) return;
                        item = _ring[i % _ring.Length]!;
                        _ring[i % _ring.Length] = null;
                    }
                    if (item is SharedSlot slot) _sink.SubmitShared(i, slot); else _sink.Submit(i, (byte[])item);
                    lock (_gate)
                    {
                        if (item is SharedSlot s2) _gpu!.Return(s2); else _pool.Push((byte[])item);
                        // Next frame of this thread: the next one in the chunk, or the start of its next chunk (else the end).
                        long nextFrame = i + 1 < hi ? i + 1 : (_encThreads == 1 ? _count : Math.Min(a + (long)_encThreads * step, _count));
                        _cursors[t] = (int)nextFrame;
                        int m = _count;
                        for (int k = 0; k < _cursors.Length; k++) if (_cursors[k] < m) m = _cursors[k];
                        _next = m;
                        Monitor.PulseAll(_gate);
                    }
                    try { _onProgress(); } catch { }
                    if (t == 0 && _cfg.Adaptive) Adapt(i);
                }
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    // ---- Adaptive window ----
    // Once a second: look at (a) the fraction of time workers were blocked on a full window, (b) the memory budget and
    // (c) signs of pressure, then grow or shrink the window. Only _window changes (the ring stays MaxWindow long);
    // shrinking never stalls frames already past the new edge, only new starts wait.
    private long _adaptLast, _adaptWaitPrev, _lastShrinkTs;
    private int _adaptCap;                      // current ceiling (lowered under pressure)
    private const double WaitGrowFrac = 0.05, VramPressure = 0.90, VramGrowLimit = 0.80;
    private const long MemReserve = 3L * 1024 * 1024 * 1024;

    private void Adapt(int encoded)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _adaptLast < Stopwatch.Frequency) return;
        double dt = (now - _adaptLast) / (double)Stopwatch.Frequency;
        long wt = Interlocked.Read(ref _workerWaitTicks);
        int active = Math.Max(1, Volatile.Read(ref _activeWorkers));
        double frac = (wt - _adaptWaitPrev) / (double)Stopwatch.Frequency / (dt * active);
        _adaptLast = now; _adaptWaitPrev = wt;
        if (_adaptCap == 0) _adaptCap = _cfg.MaxWindow;

        int w = _window;
        int inflight;
        lock (_gate) inflight = _rendered - _next;
        long frameBytes = (long)_scene.Timeline.VideoInfo.Width * _scene.Timeline.VideoInfo.Height * 4;
        double usedMb = 0, budMb = 0, availMb = 0;
        bool pressure = false; string why = "";
        long roomBytes;   // headroom for growing the window
        if (_gpu is not null)
        {
            var vm = Vram.Query();
            if (vm is { } v)
            {
                usedMb = v.usage / 1048576.0; budMb = v.budget / 1048576.0;
                if (v.budget > 0 && v.usage > v.budget * VramPressure) { pressure = true; why = "vram>90%"; }
                roomBytes = (long)Math.Min(_cfg.GpuBudgetBytes - (long)_gpu.Created * frameBytes, (long)(v.budget * VramGrowLimit - v.usage));
            }
            else roomBytes = _cfg.GpuBudgetBytes - (long)_gpu.Created * frameBytes;
            if (_gpu.Pressure) { pressure = true; why = "slot-create-failure"; }
        }
        else
        {
            var (_, avail) = SysMem.Query();
            availMb = avail / 1048576.0;
            if (Vram.Query() is { } v2) { usedMb = v2.usage / 1048576.0; budMb = v2.budget / 1048576.0; }
            if (avail > 0 && avail < (ulong)MemReserve / 3) { pressure = true; why = "mem<1GB"; }
            roomBytes = (long)avail - MemReserve;
        }

        string note = "";
        if (pressure)
        {
            int nw = Math.Max(Math.Max(_workers * 2, _cfg.Window / 4), w * 3 / 4);
            if (nw < w)
            {
                _window = nw; _adaptCap = nw; _lastShrinkTs = now; note = "shrink:" + why;
                if (_gpu is not null) _gpu.SetCap(Math.Max(nw, _gpu.Created));
                lock (_gate) Monitor.PulseAll(_gate);
            }
            else note = "pressure:" + why;
        }
        else if (frac > WaitGrowFrac && w < _adaptCap && (now - _lastShrinkTs) > 10 * Stopwatch.Frequency && roomBytes > 0)
        {
            int nw = Math.Min(_adaptCap, Math.Max(w + _workers, w * 3 / 2));
            nw = (int)Math.Min(nw, w + roomBytes / frameBytes);
            if (nw > w)
            {
                _window = nw; note = "grow";
                lock (_gate) Monitor.PulseAll(_gate);
            }
        }
        if (note != "")
            Log.Write($"window {w} -> {_window} ({note}) wait={frac * 100:F1}% inflight={inflight} encoded={encoded} vram={usedMb:F0}/{budMb:F0}MB availmem={availMb:F0}MB");
    }

    public void Dispose()
    {
        Stop();
        foreach (var t in _encodeThreads) t.Join();
        foreach (var t in _threads) t?.Join();
        TachieLink.SetMultiDevice(false);
        lock (_gate) { _pool.Clear(); Array.Clear(_ring); }
        _gpu?.Dispose();
    }
}
