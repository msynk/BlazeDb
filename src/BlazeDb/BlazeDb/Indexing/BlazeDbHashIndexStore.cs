namespace BlazeDb;

// TIndexKey is intentionally unconstrained so nullable properties can be indexed;
// null keys are filtered out before reaching the dictionary.
#pragma warning disable CS8714
internal sealed class BlazeDbHashIndexStore<TRow, TIndexKey> : IBlazeDbIndexStore<TRow>
{
    private readonly BlazeDbHashIndexDefinition<TRow, TIndexKey> _definition;
    private readonly Dictionary<TIndexKey, Bucket> _buckets;

    public BlazeDbHashIndexStore(BlazeDbHashIndexDefinition<TRow, TIndexKey> definition)
    {
        _definition = definition;
        _buckets = new Dictionary<TIndexKey, Bucket>(definition.Comparer);
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
            _buckets[key] = bucket = new Bucket();
        }
        bucket.Add(seq, row);
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
        if (bucket.HasEntryOtherThan(replacedSeq))
        {
            throw new BlazeDbUniqueConstraintViolationException(tableName, _definition.Name, key);
        }
    }

    public bool EntryMatches(TRow row, long seq)
    {
        var key = _definition.Selector(row);
        return key is null || (_buckets.TryGetValue(key, out var bucket) && bucket.Contains(seq));
    }

    public IEnumerable<TRow> Lookup(TIndexKey key)
    {
        if (key is null)
        {
            return [];
        }
        // Resolved when enumeration starts, so an enumerable obtained before a write sees the table
        // as it is when it is walked - the same deferred semantics as Scan and Range.
        return LookupCore(key);
    }

    private IEnumerable<TRow> LookupCore(TIndexKey key)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            yield break;
        }
        foreach (var row in bucket.Rows())
        {
            yield return row;
        }
    }

    public int CountOf(TIndexKey key) =>
        key is not null && _buckets.TryGetValue(key, out var bucket) ? bucket.Count : 0;

    public IEnumerable<TRow> LookupBoxed(object key) => Lookup((TIndexKey)key);

    public IEnumerable<TRow> RangeBoxed(bool hasFrom, object? from, bool hasTo, object? to, bool descending) =>
        throw new NotSupportedException(
            $"Index '{_definition.Name}' is a hash index and answers equality only. " +
            "Declare it with [BlazeDbOrderedIndex] to scan ranges.");

    /// <summary>
    /// The rows sharing one indexed value, keyed by row sequence number. Most values - every value
    /// of a unique index, and most values of a selective one - map to a single row, so the bucket
    /// holds that one entry inline and only grows a dictionary once a second row arrives. That keeps
    /// a high-cardinality index at one small object per distinct value instead of one hash table
    /// per distinct value, which matters when the whole database lives in a browser tab's memory.
    /// </summary>
    private sealed class Bucket
    {
        private bool _hasInline;
        private long _seq;
        private TRow _row = default!;
        private Dictionary<long, TRow>? _many;

        public int Count => _many?.Count ?? (_hasInline ? 1 : 0);

        public void Add(long seq, TRow row)
        {
            if (_many is not null)
            {
                _many[seq] = row;
            }
            else if (!_hasInline || _seq == seq)
            {
                (_hasInline, _seq, _row) = (true, seq, row);
            }
            else
            {
                _many = new Dictionary<long, TRow> { [_seq] = _row, [seq] = row };
                (_hasInline, _row) = (false, default!);
            }
        }

        public bool Remove(long seq)
        {
            if (_many is not null)
            {
                return _many.Remove(seq);
            }
            if (!_hasInline || _seq != seq)
            {
                return false;
            }
            (_hasInline, _row) = (false, default!);
            return true;
        }

        public bool Contains(long seq) =>
            _many is not null ? _many.ContainsKey(seq) : _hasInline && _seq == seq;

        public bool HasEntryOtherThan(long seq)
        {
            if (_many is null)
            {
                return _hasInline && _seq != seq;
            }
            foreach (var other in _many.Keys)
            {
                if (other != seq)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The rows as of now; the caller (LookupCore) only asks once enumeration has begun.</summary>
        public IEnumerable<TRow> Rows() =>
            _many is not null ? _many.Values : _hasInline ? [_row] : [];
    }
}
#pragma warning restore CS8714
