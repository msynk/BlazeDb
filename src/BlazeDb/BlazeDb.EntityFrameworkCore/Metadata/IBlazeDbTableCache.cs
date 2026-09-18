using Microsoft.EntityFrameworkCore.Metadata;

namespace BlazeDb.EntityFrameworkCore.Metadata;

/// <summary>Maps entity types onto the engine's tables.</summary>
internal interface IBlazeDbTableCache
{
    /// <summary>The engine instance the context was configured with.</summary>
    BlazeDbDatabase Database { get; }

    IBlazeDbTableBinding GetBinding(IEntityType entityType);

    /// <summary>Drops the cached engine so the next access reopens after <c>EnsureDeleted</c>.</summary>
    void Reset();
}
