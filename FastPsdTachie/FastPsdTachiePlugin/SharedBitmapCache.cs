using System.Diagnostics;
using System.Threading;

namespace FastPsdTachiePlugin;

/// <summary>The frame-independent part that identifies what is cached (compared by value).</summary>
internal sealed class PlanIdentity : IEquatable<PlanIdentity>
{
    public readonly int DeviceId;     // per render device (ID2D1Device)
    public readonly string Path;      // normalized full path
    public readonly long StampTicks;  // file mtime
    public readonly long Length;      // file size
    public readonly int Width, Height;
    public readonly ulong Fp1, Fp2;   // order-independent 128-bit hash of the base/removed sets
    private readonly int _hash;

    public PlanIdentity(int deviceId, string path, long stampTicks, long length, int width, int height, ulong fp1, ulong fp2)
    {
        DeviceId = deviceId; Path = path; StampTicks = stampTicks; Length = length;
        Width = width; Height = height; Fp1 = fp1; Fp2 = fp2;
        var h = new HashCode();
        h.Add(deviceId); h.Add(path, StringComparer.OrdinalIgnoreCase); h.Add(stampTicks); h.Add(length);
        h.Add(width); h.Add(height); h.Add(fp1); h.Add(fp2);
        _hash = h.ToHashCode();
    }

    public bool Equals(PlanIdentity? o)
        => o is not null && (ReferenceEquals(this, o) ||
           (_hash == o._hash && DeviceId == o.DeviceId && StampTicks == o.StampTicks && Length == o.Length
            && Width == o.Width && Height == o.Height && Fp1 == o.Fp1 && Fp2 == o.Fp2
            && string.Equals(Path, o.Path, StringComparison.OrdinalIgnoreCase)));

    public override bool Equals(object? obj) => Equals(obj as PlanIdentity);
    public override int GetHashCode() => _hash;

    private PlanIdentity? _deviceFree;
    /// <summary>Same identity without the device (key for cross-device sharing; DeviceId = 0).</summary>
    public PlanIdentity DeviceFree => _deviceFree ??= new PlanIdentity(0, Path, StampTicks, Length, Width, Height, Fp1, Fp2);
}

/// <summary>Cache key: (plan identity, pick per animation slot).</summary>
internal readonly struct StateKey : IEquatable<StateKey>
{
    public readonly PlanIdentity Plan;
    public readonly string?[] Picks;
    private readonly int _hash;

    public StateKey(PlanIdentity plan, string?[] picks)
    {
        Plan = plan;
        Picks = picks;
        var h = new HashCode();
        h.Add(plan.GetHashCode());
        foreach (var p in picks) h.Add(p is null ? 0 : p.GetHashCode());
        _hash = h.ToHashCode();
    }

    public bool Equals(StateKey o)
    {
        if (_hash != o._hash || Picks.Length != o.Picks.Length || !Plan.Equals(o.Plan)) return false;
        for (int i = 0; i < Picks.Length; i++)
            if (!string.Equals(Picks[i], o.Picks[i], StringComparison.Ordinal)) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is StateKey k && Equals(k);
    public override int GetHashCode() => _hash;
}

internal sealed class CacheEntry<T> where T : class
{
    public StateKey Key;
    public T Value = null!;
    public long Bytes;
    public int Pins;                 // sources holding it on display (evictable only at 0)
    public bool Detached;            // removed from the map but still in use
    public LinkedListNode<CacheEntry<T>>? Node;
}

internal sealed class CacheStats
{
    public long Hits, Misses, Composes, Waits, WaitTimeouts, ComposeFailures, Evictions, Purged, Shrinks;
}

/// <summary>
/// Process-wide cache of composed bitmaps (pure logic, no D2D).
/// - The limit (count/bytes) is global; when exceeded, least recently used entries that are not in use (Pins == 0) are evicted.
/// - Evicted or purged bitmaps are disposed only after a number of Update calls (Tick), since a frame already handed to the renderer may still reference them.
/// - Concurrent misses on one key are composed by a single thread while the others wait. Composition is serialized globally to be safe.
/// </summary>
internal sealed class SharedBitmapCache<T> where T : class
{
    private sealed class Inflight
    {
        public readonly ManualResetEventSlim Done = new(false);
        public CacheEntry<T>? Result;
        public int Waiters;
    }

    private readonly object _gate = new();
    private readonly object _composeLock = new();
    private readonly Dictionary<StateKey, CacheEntry<T>> _map = new();
    private readonly LinkedList<CacheEntry<T>> _lru = new();
    private readonly Dictionary<StateKey, Inflight> _inflight = new();
    private readonly Queue<(T Value, long Due)> _graveyard = new();
    private readonly Dictionary<int, int> _deviceSources = new();
    // When a device's last source is disposed its images are not dropped at once: if a new source registers on the same device within
    // the grace period they stay usable (ParallelRender recreates sources per chunk while the worker's device lives on, so the count
    // often hits 0 for a moment). Device IDs are never reused, so images of a destroyed device cannot be hit by mistake.
    private readonly Dictionary<int, long> _deviceIdleSince = new();
    private readonly long _deviceGraceTicks;
    private Timer? _graceTimer;
    private readonly Action<T> _dispose;
    private int _maxCount;
    private long _maxBytes;
    private readonly long _minBytes;
    private readonly int _waitTimeoutMs;
    private long _tick;
    private int _graveCount;
    private int _sources;
    private long _bytes;

    public readonly CacheStats Stats = new();

    /// <summary>Raised when the registered source count reaches 0 (every source disposed).</summary>
    public event Action? BecameIdle;

    public SharedBitmapCache(int maxCount, long maxBytes, Action<T> dispose, int waitTimeoutMs = 30000, long minBytes = 32L * 1024 * 1024, double deviceGraceSec = 0)
    {
        _deviceGraceTicks = (long)(Math.Max(0, deviceGraceSec) * Stopwatch.Frequency);
        _minBytes = Math.Min(minBytes, maxBytes);
        _maxCount = maxCount; _maxBytes = maxBytes; _dispose = dispose; _waitTimeoutMs = waitTimeoutMs;
    }

    public long MaxBytes { get { lock (_gate) return _maxBytes; } }

    /// <summary>
    /// Shrink under VRAM pressure: halves the limit (of the smaller of current usage and limit) and evicts by LRU, skipping entries in use.
    /// Never goes below minBytes. Returns true if it shrank.
    /// </summary>
    public bool Shrink()
    {
        lock (_gate)
        {
            long target = Math.Max(_minBytes, Math.Min(_maxBytes, _bytes) / 2);
            if (target >= _maxBytes && _maxBytes <= _minBytes) return false;
            _maxBytes = Math.Min(_maxBytes, target);
            Stats.Shrinks++;
            EvictIfNeeded();
            return true;
        }
    }

    /// <summary>Number of devices with registered sources.</summary>
    public int DeviceCount { get { lock (_gate) return _deviceSources.Count; } }

    public void Register(int deviceId)
    {
        lock (_gate)
        {
            _sources++;
            _deviceIdleSince.Remove(deviceId);
            _deviceSources[deviceId] = _deviceSources.GetValueOrDefault(deviceId) + 1;
        }
    }

    public void Unregister(int deviceId)
    {
        bool idle = false;
        lock (_gate)
        {
            _sources--;
            int n = _deviceSources.GetValueOrDefault(deviceId) - 1;
            if (n <= 0)
            {
                _deviceSources.Remove(deviceId);
                if (_deviceGraceTicks <= 0) PurgeWhere(e => e.Key.Plan.DeviceId == deviceId);   // images of a device must not outlive it
                else
                {
                    _deviceIdleSince[deviceId] = Stopwatch.GetTimestamp();
                    _graceTimer ??= new Timer(_ => PurgeIdleDevices(), null, 1000, 1000);
                }
            }
            else _deviceSources[deviceId] = n;
            if (_sources <= 0)
            {
                _sources = 0;
                // No source can reference them any more, so dispose without waiting.
                while (_graveyard.Count > 0) _dispose(_graveyard.Dequeue().Value);
                _graveCount = 0;
                idle = true;
            }
        }
        if (idle) BecameIdle?.Invoke();
    }

    /// <summary>Drops images of devices whose source count stayed 0 past the grace period.</summary>
    private void PurgeIdleDevices()
    {
        lock (_gate)
        {
            if (_deviceIdleSince.Count == 0) return;
            long now = Stopwatch.GetTimestamp();
            List<int>? due = null;
            foreach (var (dev, since) in _deviceIdleSince)
                if (now - since >= _deviceGraceTicks) (due ??= new()).Add(dev);
            if (due is null) return;
            foreach (int dev in due)
            {
                _deviceIdleSince.Remove(dev);
                if (_deviceSources.ContainsKey(dev)) continue;
                PurgeWhere(e => e.Key.Plan.DeviceId == dev);
            }
        }
    }

    /// <summary>Called on every source Update; disposes graveyard entries whose delay has elapsed.</summary>
    public void Tick()
    {
        long t = Interlocked.Increment(ref _tick);
        if (Volatile.Read(ref _graveCount) == 0) return;
        lock (_gate)
        {
            while (_graveyard.Count > 0 && _graveyard.Peek().Due <= t)
            {
                _dispose(_graveyard.Dequeue().Value);
                _graveCount--;
            }
        }
    }

    /// <summary>On a hit, pins and returns the entry (the caller must Release it).</summary>
    public CacheEntry<T>? TryAcquire(StateKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var e)) return null;
            e.Pins++;
            Touch(e);
            Stats.Hits++;
            return e;
        }
    }

    /// <summary>
    /// On a miss: waits if the same key is being composed, otherwise composes. Returns the entry pinned on success.
    /// Null on failure (compose returned null, or the wait timed out). An exception propagates to the composing caller; waiters get null.
    /// </summary>
    public CacheEntry<T>? Acquire(StateKey key, Func<(T? Value, long Bytes)> compose, out bool composedHere)
    {
        composedHere = false;
        Inflight f;
        bool owner;
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var hit))
            {
                hit.Pins++; Touch(hit); Stats.Hits++;
                return hit;
            }
            if (_inflight.TryGetValue(key, out f!)) { owner = false; f.Waiters++; Stats.Waits++; }
            else { f = new Inflight(); _inflight[key] = f; owner = true; Stats.Misses++; }
        }

        if (!owner)
        {
            if (!f.Done.Wait(_waitTimeoutMs))
            {
                lock (_gate)
                {
                    if (f.Done.IsSet || f.Result is not null) return f.Result;   // finished just now: take the pin already granted
                    f.Waiters--;                                                 // revoke the promised pin
                    Stats.WaitTimeouts++;
                    return null;
                }
            }
            return f.Result;   // pins for all waiters were granted on completion
        }

        (T? Value, long Bytes) made = default;
        Exception? error = null;
        try
        {
            lock (_composeLock) made = compose();
        }
        catch (Exception ex) { error = ex; }

        CacheEntry<T>? entry = null;
        lock (_gate)
        {
            _inflight.Remove(key);
            if (made.Value is not null && error is null)
            {
                entry = new CacheEntry<T> { Key = key, Value = made.Value, Bytes = made.Bytes };
                entry.Pins = 1 + f.Waiters;            // the composer plus the waiters
                // Older versions of the same PSD (different mtime/size) are no longer needed.
                PurgeWhere(e => e.Key.Plan.DeviceId == key.Plan.DeviceId
                    && string.Equals(e.Key.Plan.Path, key.Plan.Path, StringComparison.OrdinalIgnoreCase)
                    && (e.Key.Plan.StampTicks != key.Plan.StampTicks || e.Key.Plan.Length != key.Plan.Length));
                entry.Node = _lru.AddFirst(entry);
                _map[key] = entry;
                _bytes += entry.Bytes;
                Stats.Composes++;
                EvictIfNeeded();
            }
            else Stats.ComposeFailures++;
            f.Result = entry;
        }
        f.Done.Set();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        composedHere = entry is not null;
        return entry;
    }

    public void Release(CacheEntry<T>? e)
    {
        if (e is null) return;
        lock (_gate)
        {
            if (--e.Pins <= 0)
            {
                e.Pins = 0;
                if (e.Detached) Bury(e.Value);
                else EvictIfNeeded();
            }
        }
    }

    public (int Entries, long Bytes, int Graveyard) Snapshot()
    {
        lock (_gate) return (_map.Count, _bytes, _graveyard.Count);
    }

    // ---- the following are called with _gate held ----

    private void Touch(CacheEntry<T> e)
    {
        if (e.Node is not null && !ReferenceEquals(_lru.First, e.Node)) { _lru.Remove(e.Node); _lru.AddFirst(e.Node); }
    }

    private void Bury(T value)
    {
        _graveyard.Enqueue((value, Volatile.Read(ref _tick) + 3L * Math.Max(1, _sources) + 3));
        _graveCount++;
    }

    private void Remove(CacheEntry<T> e)
    {
        if (e.Node is not null) _lru.Remove(e.Node);
        e.Node = null;
        _map.Remove(e.Key);
        _bytes -= e.Bytes;
        if (e.Pins == 0) Bury(e.Value);
        else e.Detached = true;
    }

    private void PurgeWhere(Func<CacheEntry<T>, bool> pred)
    {
        foreach (var e in _lru.Where(pred).ToList()) { Remove(e); Stats.Purged++; }
    }

    private void EvictIfNeeded()
    {
        var node = _lru.Last;
        while ((_map.Count > _maxCount || _bytes > _maxBytes) && node is not null)
        {
            var prev = node.Previous;
            if (node.Value.Pins == 0)
            {
                Remove(node.Value);
                Stats.Evictions++;
            }
            node = prev;
        }
    }
}

/// <summary>How the default cache limit is chosen (pure logic).</summary>
internal static class CachePolicy
{
    public const long MinBytes = 256L * 1024 * 1024;
    public const long MaxBytes = 3L * 1024 * 1024 * 1024;
    public const long FallbackBytes = 1024L * 1024 * 1024;
    public const int DefaultCount = 512;

    /// <summary>budget = DXGI Budget of the Local segment; null if unavailable.</summary>
    public static long DefaultBytes(ulong? budget)
    {
        if (budget is null or 0) return FallbackBytes;
        double v = budget.Value * 0.20;
        return (long)Math.Clamp(v, (double)MinBytes, (double)MaxBytes);
    }
}
