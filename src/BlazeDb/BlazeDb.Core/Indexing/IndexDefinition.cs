namespace BlazeDb;

/// <summary>
/// Definition of a secondary index over rows of type <typeparamref name="TRow"/>.
/// Concrete implementations: <c>HashIndexDefinition</c> (equality lookups) and
/// <c>OrderedIndexDefinition</c> (range scans). Stores are maintained automatically by the
/// owning table on every mutation.
/// </summary>
public abstract class IndexDefinition<TRow>
{
    protected IndexDefinition(string name, bool unique = false, IReadOnlyList<string>? members = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Index name must be non-empty.", nameof(name));
        }
        Name = name;
        IsUnique = unique;
        Members = members is { Count: > 0 } ? members : [name];
    }

    public string Name { get; }

    /// <summary>
    /// Row properties this index covers, in order. Query layers use it to decide whether a
    /// predicate can be served by the index. It defaults to the index name, which is the name the
    /// source generator gives an index over a single property.
    /// </summary>
    public IReadOnlyList<string> Members { get; }

    /// <summary>
    /// When true, no two rows may share an indexed value. Rows whose indexed value is null are
    /// exempt, matching the treatment of nulls everywhere else in the indexing layer.
    /// </summary>
    public bool IsUnique { get; }

    internal abstract IIndexStore<TRow> CreateStore();
}

/// <summary>Runtime state of one secondary index inside a table.</summary>
internal interface IIndexStore<TRow>
{
    /// <summary>The definition this store was created from.</summary>
    object Definition { get; }

    void Add(TRow row, long seq);

    void Remove(TRow row, long seq);

    void Clear();

    /// <summary>
    /// Throws <see cref="UniqueConstraintViolationException"/> if storing <paramref name="row"/>
    /// would duplicate an indexed value. <paramref name="replacedSeq"/> is the sequence number of
    /// the row being replaced (0 when inserting), whose entry is about to be removed and so must
    /// not count as a conflict. Called before any mutation is applied.
    /// </summary>
    void ValidateUnique(TRow row, long replacedSeq, string tableName);

    /// <summary>
    /// Equality lookup with the key passed as <see cref="object"/>. For callers that pick an index
    /// at runtime — the EF Core provider translating a LINQ predicate — and so cannot name the key
    /// type. The cast happens inside the already-instantiated generic store, so no reflection or
    /// runtime code generation is involved.
    /// </summary>
    IEnumerable<TRow> LookupBoxed(object key);

    /// <summary>
    /// Range scan with boxed bounds; see <see cref="LookupBoxed"/>. Only ordered indexes support
    /// it — a hash index throws.
    /// </summary>
    IEnumerable<TRow> RangeBoxed(bool hasFrom, object? from, bool hasTo, object? to, bool descending);
}
