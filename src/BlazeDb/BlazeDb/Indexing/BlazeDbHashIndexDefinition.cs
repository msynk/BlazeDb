namespace BlazeDb;

/// <summary>
/// Equality (hash) secondary index: O(1) lookup of all rows whose indexed value equals a key.
/// Rows whose indexed value is null are not indexed.
/// </summary>
public sealed class BlazeDbHashIndexDefinition<TRow, TIndexKey> : BlazeDbIndexDefinition<TRow>
{
    public BlazeDbHashIndexDefinition(
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

    internal override IBlazeDbIndexStore<TRow> CreateStore() => new BlazeDbHashIndexStore<TRow, TIndexKey>(this);
}
