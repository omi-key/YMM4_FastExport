using System.Diagnostics;

namespace FastPsdTachiePlugin;

/// <summary>
/// Process-wide store of parsed documents (PSD layer structure plus CPU-side pixels; no D2D). Pure logic.
/// Parallel rendering recreates sources per worker (device) x chunk; this makes each PSD load (whole-file read + structure parse) happen once.
/// - Key: (normalized path, mtime, size). An updated PSD gets a new key; the old version is disposed when its last user is gone.
/// - Reference counted; documents in use (Refs &gt; 0) are never evicted. Documents whose users dropped to 0 stay "idle" (the count
///   briefly hits 0 at chunk switches) and are disposed oldest-first when the idle total exceeds the limit, or after a TTL.
/// - Concurrent loads of one key run on a single thread while others wait. A failed load returns null and is not remembered (the next request retries).
/// </summary>
internal sealed class SharedDocStore<T> where T : class
{
    internal readonly record struct DocKey(string Path, long StampTicks, long Length);

    private sealed class DocKeyComparer : IEqualityComparer<DocKey>
    {
        public bool Equals(DocKey a, DocKey b) => a.StampTicks == b.StampTicks && a.Length == b.Length && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(DocKey k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.StampTicks, k.Length);
    }

    /// <summary>Proof of a borrow; hand it back to Release.</summary>
    public sealed class Lease
    {
        internal Entry E = null!;
        internal bool Released;
        public T Value => E.Value!;
    }

    internal sealed class Entry
    {
        public DocKey Key;
        public readonly object LoadGate = new();
        public T? Value;
        public long Bytes;
        public bool Loaded, Failed;
        public int Refs;
        public bool Superseded;      // a newer version of the same path appeared
        public long IdleSince;       // Stopwatch time when it became idle
        public LinkedListNode<Entry>? IdleNode;
    }

    private readonly object _gate = new();
    private readonly Dictionary<DocKey, Entry> _map = new(new DocKeyComparer());
    private readonly LinkedList<Entry> _idle = new();   // first = most recently idle
    private readonly Action<T> _dispose;
    private readonly long _maxIdleBytes;
    private readonly long _idleTtlTicks;
    private long _idleBytes;
    private Timer? _timer;

    public long Loads, LoadFailures, Hits, Waits, Evictions;

    public SharedDocStore(Action<T> dispose, long maxIdleBytes, TimeSpan idleTtl)
    {
        _dispose = dispose; _maxIdleBytes = maxIdleBytes;
        _idleTtlTicks = (long)(idleTtl.TotalSeconds * Stopwatch.Frequency);
    }

    public (int Entries, int Idle, long IdleBytes) Snapshot() { lock (_gate) return (_map.Count, _idle.Count, _idleBytes); }

    /// <summary>
    /// Acquires the document, loading it with <paramref name="load"/> if absent (concurrent requests for a key load once).
    /// Null if load returns null or throws. load returns (value, approximate bytes).
    /// </summary>
    public Lease? Acquire(DocKey key, Func<(T Value, long Bytes)?> load)
    {
        Entry e;
        bool created = false;
        var dead = new List<T>();
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out e!))
            {
                e = new Entry { Key = key };
                _map[key] = e;
                created = true;
                // Older versions of the same path go at once if unused, else when their last user releases.
                foreach (var o in _map.Values.Where(o => !ReferenceEquals(o, e) && !o.Superseded
                             && string.Equals(o.Key.Path, key.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    o.Superseded = true;
                    if (o.Refs == 0 && o.Loaded && Remove(o) is { } v) dead.Add(v);
                }
            }
            e.Refs++;
            if (e.IdleNode is not null) { _idle.Remove(e.IdleNode); e.IdleNode = null; _idleBytes -= e.Bytes; }
        }

        foreach (var v in dead) DisposeQuietly(v);
        bool ok;
        lock (e.LoadGate)
        {
            if (!e.Loaded && !e.Failed)
            {
                try
                {
                    var r = load();
                    if (r is { } v) { e.Value = v.Value; e.Bytes = Math.Max(1, v.Bytes); e.Loaded = true; Interlocked.Increment(ref Loads); }
                    else { e.Failed = true; Interlocked.Increment(ref LoadFailures); }
                }
                catch { e.Failed = true; Interlocked.Increment(ref LoadFailures); }
            }
            else if (!created) Interlocked.Increment(ref Waits);
            ok = e.Loaded;
        }
        if (!ok)
        {
            lock (_gate)
            {
                e.Refs--;
                if (e.Refs == 0 && _map.TryGetValue(e.Key, out var cur) && ReferenceEquals(cur, e)) _map.Remove(e.Key);   // do not keep failures
            }
            return null;
        }
        if (!created) Interlocked.Increment(ref Hits);
        return new Lease { E = e };
    }

    public void Release(Lease? l)
    {
        if (l is null) return;
        var dead = new List<T>();
        lock (_gate)
        {
            if (l.Released) return;
            l.Released = true;
            var e = l.E;
            if (--e.Refs > 0) return;
            e.Refs = 0;
            if (e.Superseded) { if (Remove(e) is { } v) dead.Add(v); }
            else
            {
                e.IdleSince = Stopwatch.GetTimestamp();
                e.IdleNode = _idle.AddFirst(e);
                _idleBytes += e.Bytes;
                while (_idleBytes > _maxIdleBytes && _idle.Last is { } n)
                    if (Remove(n.Value) is { } v) dead.Add(v);
                if (_idle.Count > 0) ArmTimer();
            }
        }
        foreach (var v in dead) DisposeQuietly(v);
    }

    /// <summary>Disposes every idle document.</summary>
    public void PurgeIdle()
    {
        List<T> list = new();
        lock (_gate)
        {
            foreach (var e in _idle.ToList()) if (Remove(e) is { } v) list.Add(v);
        }
        foreach (var v in list) DisposeQuietly(v);
    }

    // ---- call with _gate held ----

    /// <summary>Removes the entry from the map and idle list and returns the value to dispose.</summary>
    private T? Remove(Entry e)
    {
        if (e.IdleNode is not null) { _idle.Remove(e.IdleNode); e.IdleNode = null; _idleBytes -= e.Bytes; }
        if (_map.TryGetValue(e.Key, out var cur) && ReferenceEquals(cur, e)) _map.Remove(e.Key);
        Evictions++;
        var v = e.Value; e.Value = null;
        return v;
    }

    private void DisposeQuietly(T? v)
    {
        if (v is null) return;
        try { _dispose(v); } catch { }
    }

    private void ArmTimer()
    {
        if (_idleTtlTicks <= 0) return;
        _timer ??= new Timer(_ => Sweep(), null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(TimeSpan.FromSeconds(Math.Max(1, _idleTtlTicks / Stopwatch.Frequency / 2.0)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>Disposes idle documents past the TTL and re-arms the timer if any remain.</summary>
    internal void Sweep()
    {
        List<T> list = new();
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            while (_idle.Last is { } n && now - n.Value.IdleSince >= _idleTtlTicks)
                if (Remove(n.Value) is { } v) list.Add(v);
            if (_idle.Count > 0) ArmTimer();
        }
        foreach (var v in list) DisposeQuietly(v);
    }
}
