namespace FastPsdTachiePlugin;

/// <summary>
/// CPU-memory store of pixel data for sharing composed results across devices (pure logic, no D2D).
/// A composed bitmap belongs to its device, so the first device to compose reads it back and Puts it, and other devices build
/// their own bitmap from the data they Get (faster than composing).
/// - Limited by bytes; least recently used entries are evicted (an eviction only makes some device compose again).
/// - Stored data is immutable; references already handed out stay valid after eviction (the GC reclaims them).
/// </summary>
internal sealed class CrossDeviceStore<T> where T : class
{
    private sealed class Entry
    {
        public StateKey Key;
        public T Value = null!;
        public long Bytes;
        public LinkedListNode<Entry>? Node;
    }

    private readonly object _gate = new();
    private readonly Dictionary<StateKey, Entry> _map = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;
    private long _maxBytes;

    public long Hits, Misses, Puts, Evictions, Rejected;

    public CrossDeviceStore(long maxBytes) { _maxBytes = maxBytes; }

    public (int Entries, long Bytes) Snapshot() { lock (_gate) return (_map.Count, _bytes); }

    public T? TryGet(StateKey key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var e)) { Misses++; return null; }
            Hits++;
            if (!ReferenceEquals(_lru.First, e.Node)) { _lru.Remove(e.Node!); _lru.AddFirst(e.Node!); }
            return e.Value;
        }
    }

    /// <summary>Stores the value. An existing key is kept (first writer wins). Returns false if a single entry exceeds the limit.</summary>
    public bool Put(StateKey key, T value, long bytes)
    {
        lock (_gate)
        {
            if (_map.ContainsKey(key)) return true;
            if (bytes > _maxBytes) { Rejected++; return false; }
            var e = new Entry { Key = key, Value = value, Bytes = bytes };
            e.Node = _lru.AddFirst(e);
            _map[key] = e;
            _bytes += bytes;
            Puts++;
            while (_bytes > _maxBytes && _lru.Last is { } last && !ReferenceEquals(last, e.Node))
            {
                _lru.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
                Evictions++;
            }
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) { _map.Clear(); _lru.Clear(); _bytes = 0; }
    }
}
