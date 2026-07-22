namespace Turso.Core.Storage;

/// <summary>
/// A pager-owned bounded LRU cache for clean main-database page images.
/// </summary>
/// <remarks>
/// WAL-overlay and transaction pages are deliberately excluded: the overlay is
/// recovery state and transaction images are not durable. Cached arrays never
/// leave the pager, so callers cannot retain or mutate an evicted image.
/// </remarks>
internal sealed class SqlitePagerReadCache
{
    private readonly Dictionary<uint, Entry> _entries = [];
    private readonly LinkedList<uint> _leastToMostRecent = [];

    public SqlitePagerReadCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count => _entries.Count;

    public bool TryGetValue(uint pageNumber, out byte[] page)
    {
        if (_entries.TryGetValue(pageNumber, out var entry))
        {
            _leastToMostRecent.Remove(entry.RecencyNode);
            _leastToMostRecent.AddLast(entry.RecencyNode);
            page = entry.Page;
            return true;
        }

        page = null!;
        return false;
    }

    public void Add(uint pageNumber, byte[] page)
    {
        ArgumentNullException.ThrowIfNull(page);

        Remove(pageNumber);
        if (_entries.Count == Capacity)
        {
            var leastRecent = _leastToMostRecent.First
                ?? throw new InvalidOperationException("SQLite pager read-cache recency state is inconsistent.");
            if (!_entries.Remove(leastRecent.Value))
                throw new InvalidOperationException("SQLite pager read-cache entry state is inconsistent.");
            _leastToMostRecent.RemoveFirst();
        }

        var recencyNode = _leastToMostRecent.AddLast(pageNumber);
        _entries.Add(pageNumber, new Entry(page, recencyNode));
    }

    public void Remove(uint pageNumber)
    {
        if (!_entries.Remove(pageNumber, out var entry))
            return;

        _leastToMostRecent.Remove(entry.RecencyNode);
    }

    public void Clear()
    {
        _entries.Clear();
        _leastToMostRecent.Clear();
    }

    private sealed record Entry(byte[] Page, LinkedListNode<uint> RecencyNode);
}
