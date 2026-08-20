namespace BlazeDb;

/// <summary>
/// Ordered secondary index: keeps rows sorted by the indexed value, enabling O(log n) range
/// scans in both directions. Rows whose indexed value is null are not indexed.
/// </summary>
public sealed class BlazeDbOrderedIndexDefinition<TRow, TIndexKey> : BlazeDbIndexDefinition<TRow>
{
    public BlazeDbOrderedIndexDefinition(
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

    internal override IBlazeDbIndexStore<TRow> CreateStore() => new BlazeDbOrderedIndexStore<TRow, TIndexKey>(this);
}
