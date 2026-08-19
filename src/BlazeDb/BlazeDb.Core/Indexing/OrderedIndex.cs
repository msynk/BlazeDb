namespace BlazeDb;

/// <summary>
/// Ordered secondary index: keeps rows sorted by the indexed value, enabling O(log n) range
/// scans in both directions. Rows whose indexed value is null are not indexed.
/// </summary>
public sealed class OrderedIndexDefinition<TRow, TIndexKey> : IndexDefinition<TRow>
{
    public OrderedIndexDefinition(
        string name,
        Func<TRow, TIndexKey> selector,
        IComparer<TIndexKey>? comparer = null,
        bool unique = false,
        IReadOnlyList<string>? members = null)
        : base(name, unique, members)
    {
        Selector = selector;
        Comparer = comparer ?? Comparer<TIndexKey>.Default;
    }

    public Func<TRow, TIndexKey> Selector { get; }

    public IComparer<TIndexKey> Comparer { get; }

    internal override IIndexStore<TRow> CreateStore() => new OrderedIndexStore<TRow, TIndexKey>(this);
}

internal sealed class OrderedIndexStore<TRow, TIndexKey> : IIndexStore<TRow>
{
    // Entries are totally ordered by (indexed value, row sequence number); the sequence number
    // disambiguates duplicate index values without requiring the primary key to be comparable.
    internal readonly struct Entry
    {
        public Entry(TIndexKey key, long seq, TRow? row)
        {
            Key = key;
            Seq = seq;
            Row = row;
        }

        public TIndexKey Key { get; }

        public long Seq { get; }

        public TRow? Row { get; }
    }

    private sealed class EntryComparer : IComparer<Entry>
    {
        private readonly IComparer<TIndexKey> _keyComparer;

        public EntryComparer(IComparer<TIndexKey> keyComparer) => _keyComparer = keyComparer;

        public int Compare(Entry x, Entry y)
        {
            var byKey = _keyComparer.Compare(x.Key, y.Key);
            return byKey != 0 ? byKey : x.Seq.CompareTo(y.Seq);
        }
    }

    private readonly OrderedIndexDefinition<TRow, TIndexKey> _definition;
    private readonly SortedSet<Entry> _entries;

    public OrderedIndexStore(OrderedIndexDefinition<TRow, TIndexKey> definition)
    {
        _definition = definition;
        _entries = new SortedSet<Entry>(new EntryComparer(definition.Comparer));
    }

    public object Definition => _definition;

    public void Add(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        if (key is null)
        {
            return;
        }
        _entries.Add(new Entry(key, seq, row));
    }

    public void Remove(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        if (key is null)
        {
            return;
        }
        _entries.Remove(new Entry(key, seq, default));
    }

    public void Clear() => _entries.Clear();

    public void ValidateUnique(TRow row, long replacedSeq, string tableName)
    {
        if (!_definition.IsUnique)
        {
            return;
        }
        var key = _definition.Selector(row);
        if (key is null || _entries.Count == 0)
        {
            return;
        }
        // All entries sharing this key sort contiguously, bracketed by the sequence extremes.
        var span = _entries.GetViewBetween(
            new Entry(key, long.MinValue, default),
            new Entry(key, long.MaxValue, default));
        foreach (var entry in span)
        {
            if (entry.Seq != replacedSeq)
            {
                throw new UniqueConstraintViolationException(tableName, _definition.Name, key);
            }
        }
    }

    public bool EntryMatches(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        return key is null || _entries.Contains(new Entry(key, seq, default));
    }

    /// <summary>All rows in index order (ascending or descending).</summary>
    public IEnumerable<TRow> All(bool descending)
    {
        var source = descending ? _entries.Reverse() : _entries;
        foreach (var entry in source)
        {
            yield return entry.Row!;
        }
    }

    /// <summary>Rows whose indexed value lies in the inclusive range [from, to].</summary>
    public IEnumerable<TRow> Range(bool hasFrom, TIndexKey from, bool hasTo, TIndexKey to, bool descending)
    {
        if (_entries.Count == 0)
        {
            yield break;
        }

        var lower = hasFrom ? new Entry(from, long.MinValue, default) : _entries.Min;
        var upper = hasTo ? new Entry(to, long.MaxValue, default) : _entries.Max;
        if (_definition.Comparer.Compare(lower.Key, upper.Key) > 0)
        {
            yield break;
        }

        var view = _entries.GetViewBetween(lower, upper);
        var source = descending ? view.Reverse() : view;
        foreach (var entry in source)
        {
            yield return entry.Row!;
        }
    }

    public IEnumerable<TRow> LookupBoxed(object key) =>
        Range(true, (TIndexKey)key, true, (TIndexKey)key, descending: false);

    public IEnumerable<TRow> RangeBoxed(bool hasFrom, object? from, bool hasTo, object? to, bool descending) =>
        hasFrom || hasTo
            ? Range(hasFrom, hasFrom ? (TIndexKey)from! : default!, hasTo, hasTo ? (TIndexKey)to! : default!, descending)
            : All(descending);
}
