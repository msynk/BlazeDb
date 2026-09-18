using Microsoft.EntityFrameworkCore;

namespace BlazeDb.Demo.Data;

/// <summary>
/// The demo's EF Core surface over the same tables every other page uses. Nothing here is
/// BlazeDb-specific beyond <c>UseBlazeDb</c> at the call site - the entity types are the
/// <c>[BlazeDbTable]</c> classes the generator already emitted descriptors for.
/// </summary>
public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options)
{
    public DbSet<TodoEntry> Todos => Set<TodoEntry>();

    public DbSet<Setting> Settings => Set<Setting>();
}
