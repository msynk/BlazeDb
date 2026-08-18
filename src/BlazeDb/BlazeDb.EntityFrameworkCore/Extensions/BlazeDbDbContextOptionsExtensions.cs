using BlazeDb.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures a <see cref="DbContext"/> to run against a BlazeDb engine instance.</summary>
public static class BlazeDbDbContextOptionsExtensions
{
    /// <summary>
    /// Points the context at an already-open <see cref="BlazeDb.Database"/>. The application owns
    /// its lifetime: BlazeDb holds the whole dataset in memory behind a single writer, so the
    /// database outlives any one context and is shared by all of them.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(this DbContextOptionsBuilder optionsBuilder, BlazeDb.Database database)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(database);

        var extension = (optionsBuilder.Options.FindExtension<BlazeDbOptionsExtension>()
                         ?? new BlazeDbOptionsExtension())
            .WithDatabase(database);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, BlazeDb.Database)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, BlazeDb.Database database)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, database);
}
