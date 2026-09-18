using BlazeDb.Serialization;

namespace BlazeDb;

/// <summary>
/// Non-generic view of a table used by the database for recovery, snapshotting and replay.
/// </summary>
internal interface IBlazeDbTableInternal
{
    string Name { get; }

    int Count { get; }

    void ReplaySet(ReadOnlySpan<byte> keyBytes, ReadOnlySpan<byte> rowBytes);

    void ReplayDelete(ReadOnlySpan<byte> keyBytes);

    void WriteSnapshot(BlazeDbBufferWriter writer, BlazeDbBufferWriter scratch);

    void LoadSnapshot(ref BlazeDbBufferReader reader);

    /// <summary>Drops every row and index entry, for reloading a replica from a fresh snapshot.</summary>
    void Clear();
}
