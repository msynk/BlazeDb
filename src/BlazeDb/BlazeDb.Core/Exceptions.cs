namespace BlazeDb;

/// <summary>Base type for all BlazeDb-specific errors.</summary>
public class BlazeDbException : Exception
{
    public BlazeDbException(string message) : base(message)
    {
    }

    public BlazeDbException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Thrown when inserting a row whose primary key already exists.</summary>
public sealed class DuplicateKeyException : BlazeDbException
{
    public DuplicateKeyException(string tableName, object key)
        : base($"Table '{tableName}' already contains a row with key '{key}'.")
    {
        TableName = tableName;
        Key = key;
    }

    public string TableName { get; }

    public object Key { get; }
}

/// <summary>Thrown when a write would duplicate a value in a unique secondary index.</summary>
public sealed class UniqueConstraintViolationException : BlazeDbException
{
    public UniqueConstraintViolationException(string tableName, string indexName, object value)
        : base($"Unique index '{indexName}' on table '{tableName}' already contains value '{value}'.")
    {
        TableName = tableName;
        IndexName = indexName;
        Value = value;
    }

    public string TableName { get; }

    public string IndexName { get; }

    public object Value { get; }
}

/// <summary>Thrown when persisted data fails validation (corrupt snapshot, bad magic, etc.).</summary>
public sealed class CorruptDatabaseException : BlazeDbException
{
    public CorruptDatabaseException(string message) : base(message)
    {
    }

    public CorruptDatabaseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when persisting would exceed the origin's storage allowance. The in-memory state is
/// unaffected and the un-written log records stay buffered, so the application keeps working and
/// can retry once space has been freed.
/// </summary>
public sealed class StorageQuotaExceededException : BlazeDbException
{
    public StorageQuotaExceededException(Storage.StorageQuota quota, long requiredBytes)
        : base($"Writing {requiredBytes} bytes would exceed the storage quota " +
               $"({quota.UsageBytes} of {quota.QuotaBytes} bytes used). " +
               "The database is intact in memory; free space or reduce the data set and flush again.")
    {
        Quota = quota;
        RequiredBytes = requiredBytes;
    }

    public Storage.StorageQuota Quota { get; }

    public long RequiredBytes { get; }
}

/// <summary>Thrown when another tab / process holds the database write lock.</summary>
public sealed class DatabaseLockedException : BlazeDbException
{
    public DatabaseLockedException(string message) : base(message)
    {
    }
}
