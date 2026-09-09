using System;
using System.Collections.Generic;

namespace monogame.Planet;

internal sealed class ChunkCache : IDisposable {
    private readonly Dictionary<ChunkAddress, Entry> _entries;
    private readonly LinkedList<ChunkAddress> _useOrder = new();

    internal ChunkCache(int capacity) {
        if (capacity <= 0) {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _entries = new Dictionary<ChunkAddress, Entry>(capacity);
    }

    internal int Capacity { get; }

    internal Chunk Get(in ChunkAddress address) {
        if (!_entries.TryGetValue(address, out var entry)) {
            return null;
        }

        _useOrder.Remove(entry.UseNode);
        _useOrder.AddFirst(entry.UseNode);
        return entry.Chunk;
    }

    internal void Touch(in ChunkAddress address) {
        if (!_entries.TryGetValue(address, out var entry)) {
            return;
        }

        _useOrder.Remove(entry.UseNode);
        _useOrder.AddFirst(entry.UseNode);
    }

    internal void Add(Chunk chunk) {
        ArgumentNullException.ThrowIfNull(chunk);
        var address = chunk.Address;
        if (_entries.ContainsKey(address)) {
            throw new InvalidOperationException("The chunk is already cached.");
        }

        if (_entries.Count == Capacity) {
            var addressToRemove = _useOrder.Last!.Value;
            var entryToRemove = _entries[addressToRemove];
            _useOrder.RemoveLast();
            _entries.Remove(addressToRemove);
            entryToRemove.Chunk.Dispose();
        }

        var node = _useOrder.AddFirst(address);
        _entries.Add(address, new Entry(chunk, node));
    }

    public void Dispose() {
        foreach (var entry in _entries.Values) {
            entry.Chunk.Dispose();
        }

        _entries.Clear();
        _useOrder.Clear();
    }

    private readonly record struct Entry(
        Chunk Chunk,
        LinkedListNode<ChunkAddress> UseNode
    );
}
