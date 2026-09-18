namespace BlazeDb;

/// <summary>Thrown when another tab / process holds the database write lock.</summary>
public sealed class BlazeDbDatabaseLockedException : BlazeDbException
{
    public BlazeDbDatabaseLockedException(string message) : base(message)
    {
    }
}
