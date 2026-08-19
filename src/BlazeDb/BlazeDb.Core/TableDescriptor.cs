using BlazeDb.Serialization;

namespace BlazeDb;

public delegate void RowWriter<TRow>(BufferWriter writer, TRow row);

public delegate TRow RowReader<TRow>(ref BufferReader reader);

public delegate void KeyWriter<TKey>(BufferWriter writer, TKey key);

public delegate TKey KeyReader<TKey>(ref BufferReader reader);

/// <summary>
/// Static metadata for a table: name, key extraction, binary serialization and index
/// definitions. Descriptors are immutable and shared; typically produced by the BlazeDb
/// source generator for <c>[Table]</c>-annotated types, but they can be hand-written.
/// </summary>
public abstract class TableDescriptor
{
    protected TableDescriptor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name must be non-empty.", nameof(name));
        }
        Name = name;
    }

    public string Name { get; }

    /// <summary>
    /// The row property the key selector reads, when known - the source generator always records it.
    /// Query layers use it to send <c>row.Id == value</c> to the primary-key dictionary; null means a
    /// hand-written descriptor did not say, and such predicates are filtered instead.
    /// </summary>
    public string? KeyMember { get; protected init; }

    internal abstract ITableInternal CreateTable(Database database);
}

public sealed class TableDescriptor<TKey, TRow> : TableDescriptor
    where TKey : notnull
{
    public TableDescriptor(
        string name,
        Func<TRow, TKey> keySelector,
        RowWriter<TRow> rowWriter,
        RowReader<TRow> rowReader,
        KeyWriter<TKey> keyWriter,
        KeyReader<TKey> keyReader,
        IReadOnlyList<IndexDefinition<TRow>>? indexes = null,
        string? keyMember = null)
        : base(name)
    {
        KeySelector = keySelector;
        RowWriter = rowWriter;
        RowReader = rowReader;
        KeyWriter = keyWriter;
        KeyReader = keyReader;
        Indexes = indexes ?? [];
        KeyMember = keyMember;
    }

    public Func<TRow, TKey> KeySelector { get; }
    public RowWriter<TRow> RowWriter { get; }

    public RowReader<TRow> RowReader { get; }

    public KeyWriter<TKey> KeyWriter { get; }

    public KeyReader<TKey> KeyReader { get; }

    public IReadOnlyList<IndexDefinition<TRow>> Indexes { get; }

    internal override ITableInternal CreateTable(Database database) => new Table<TKey, TRow>(database, this);
}
