namespace BlazeDb;

/// <summary>
/// Equality (hash) secondary index: O(1) lookup of all rows whose indexed value equals a key.
/// Rows whose indexed value is null are not indexed.
/// </summary>
public sealed class HashIndexDefinition<TRow, TIndexKey> : IndexDefinition<TRow>
{
    public HashIndexDefinition(
        string name,
        Func<TRow, TIndexKey> selector,
        IEqualityComparer<TIndexKey>? comparer = null,
        bool unique = false,
        IReadOnlyList<string>? members = null)
        : base(name, unique, members)
    {
        Selector = selector;
        Comparer = comparer ?? EqualityComparer<TIndexKey>.Default;
    }

    public Func<TRow, TIndexKey> Selector { get; }

    public IEqualityComparer<TIndexKey> Comparer { get; }

    internal override IIndexStore<TRow> CreateStore() => new HashIndexStore<TRow, TIndexKey>(this);
}

// TIndexKey is intentionally unconstrained so nullable properties can be indexed;
// null keys are filtered out before reaching the dictionary.
#pragma warning disable CS8714
internal sealed class HashIndexStore<TRow, TIndexKey> : IIndexStore<TRow>
{
    private readonly HashIndexDefinition<TRow, TIndexKey> _definition;
    private readonly Dictionary<TIndexKey, Dictionary<long, TRow>> _buckets;

    public HashIndexStore(HashIndexDefinition<TRow, TIndexKey> definition)
    {
        _definition = definition;
        _buckets = new Dictionary<TIndexKey, Dictionary<long, TRow>>(definition.Comparer);
    }

    public object Definition => _definition;

    public void Add(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        if (key is null)
        {
            return;
        }
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            _buckets[key] = bucket = new Dictionary<long, TRow>();
        }
        bucket[seq] = row;
    }

    public void Remove(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        if (key is null)
        {
            return;
        }
        if (_buckets.TryGetValue(key, out var bucket) && bucket.Remove(seq) && bucket.Count == 0)
        {
            _buckets.Remove(key);
        }
    }

    public void Clear() => _buckets.Clear();

    public void ValidateUnique(TRow row, long replacedSeq, string tableName)
    {
        if (!_definition.IsUnique)
        {
            return;
        }
        var key = _definition.Selector(row);
        if (key is null || !_buckets.TryGetValue(key, out var bucket))
        {
            return;
        }
        foreach (var seq in bucket.Keys)
        {
            if (seq != replacedSeq)
            {
                throw new UniqueConstraintViolationException(tableName, _definition.Name, key);
            }
        }
    }

    public IEnumerable<TRow> Lookup(TIndexKey key)
    {
        if (key is not null && _buckets.TryGetValue(key, out var bucket))
        {
            foreach (var row in bucket.Values)
            {
                yield return row;
            }
        }
    }

    public int CountOf(TIndexKey key) =>
        key is not null && _buckets.TryGetValue(key, out var bucket) ? bucket.Count : 0;

    public IEnumerable<TRow> LookupBoxed(object key) => Lookup((TIndexKey)key);

    public IEnumerable<TRow> RangeBoxed(bool hasFrom, object? from, bool hasTo, object? to, bool descending) =>
        throw new NotSupportedException(
            $"Index '{_definition.Name}' is a hash index and answers equality only. " +
            "Declare it with [OrderedIndex] to scan ranges.");
}
#pragma warning restore CS8714
