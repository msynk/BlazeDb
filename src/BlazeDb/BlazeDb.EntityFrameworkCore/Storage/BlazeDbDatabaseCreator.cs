using Microsoft.EntityFrameworkCore.Storage;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// The database is opened and owned by the application before any context exists, so creation and
/// deletion are not the provider's to perform.
/// </summary>
public sealed class BlazeDbDatabaseCreator : IDatabaseCreator
{
    public bool EnsureCreated() => false;

    public Task<bool> EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool EnsureDeleted() => false;

    public Task<bool> EnsureDeletedAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool CanConnect() => true;

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}
