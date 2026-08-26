using System;

namespace monogame.Planet;

internal sealed class Cache : IDisposable {
    private const int MissingIndex = -1;

    private readonly Entry[] _entries;
    private int _count;
    private int _mostRecentlyUsed = MissingIndex;
    private int _leastRecentlyUsed = MissingIndex;

    internal Cache(int keyCount, int capacity) {
        if (keyCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(keyCount));
        }
        if (capacity <= 0) {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _entries = new Entry[keyCount];
        Capacity = Math.Min(capacity, keyCount);
    }

    internal int Capacity { get; }

    internal Chunk Get(int key, ref DrawCounters counters) {
        ref var entry = ref _entries[key];
        if (entry.Chunk is null) {
            counters.CacheMisses++;
            return null;
        }

        MarkMostRecentlyUsed(key);
        return entry.Chunk;
    }

    internal void Touch(int key) {
        if (_entries[key].Chunk is not null) {
            MarkMostRecentlyUsed(key);
        }
    }

    internal bool Add(int key, Chunk chunk) {
        ArgumentNullException.ThrowIfNull(chunk);
        if (_entries[key].Chunk is not null) {
            throw new InvalidOperationException("The cache key is already in use.");
        }

        var evicted = _count == Capacity;
        if (evicted) {
            EvictLeastRecentlyUsed();
        }

        ref var entry = ref _entries[key];
        entry.Chunk = chunk;
        entry.Previous = MissingIndex;
        entry.Next = _mostRecentlyUsed;
        if (_mostRecentlyUsed != MissingIndex) {
            _entries[_mostRecentlyUsed].Previous = key;
        }
        else {
            _leastRecentlyUsed = key;
        }

        _mostRecentlyUsed = key;
        _count++;
        return evicted;
    }

    public void Dispose() {
        while (_leastRecentlyUsed != MissingIndex) {
            EvictLeastRecentlyUsed();
        }
    }

    private void MarkMostRecentlyUsed(int key) {
        if (key == _mostRecentlyUsed) {
            return;
        }

        RemoveFromOrder(key);
        ref var entry = ref _entries[key];
        entry.Previous = MissingIndex;
        entry.Next = _mostRecentlyUsed;
        _entries[_mostRecentlyUsed].Previous = key;
        _mostRecentlyUsed = key;
    }

    private void RemoveFromOrder(int key) {
        ref var entry = ref _entries[key];
        if (entry.Previous != MissingIndex) {
            _entries[entry.Previous].Next = entry.Next;
        }
        else {
            _mostRecentlyUsed = entry.Next;
        }

        if (entry.Next != MissingIndex) {
            _entries[entry.Next].Previous = entry.Previous;
        }
        else {
            _leastRecentlyUsed = entry.Previous;
        }
    }

    private void EvictLeastRecentlyUsed() {
        var key = _leastRecentlyUsed;
        ref var entry = ref _entries[key];
        var chunk = entry.Chunk;
        RemoveFromOrder(key);
        entry = default;
        _count--;
        chunk.Dispose();
    }

    private struct Entry {
        internal Chunk Chunk;
        internal int Previous;
        internal int Next;
    }
}
