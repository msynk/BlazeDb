using BlazeDb.EntityFrameworkCore.Infrastructure;
using BlazeDb.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;

// ReSharper disable once CheckNamespace
namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures a <see cref="DbContext"/> to run on BlazeDb, the same way <c>UseSqlite</c> or <c>UseInMemoryDatabase</c> would.</summary>
public static class BlazeDbDbContextOptionsExtensions
{
    /// <summary>
    /// Uses an in-memory store named after the context type, so every <typeparamref name="TContext"/>
    /// shares one engine - the usual <c>AddDbContext</c> shape. Pass an explicit name or a storage
    /// backend to choose a different store.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(this DbContextOptionsBuilder optionsBuilder) =>
        UseBlazeDb(optionsBuilder, "BlazeDb");

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb(
            (DbContextOptionsBuilder)optionsBuilder,
            typeof(TContext).FullName ?? typeof(TContext).Name);

    /// <summary>
    /// Uses a named in-memory store. Contexts configured with the same name share one engine,
    /// matching <c>UseInMemoryDatabase(name)</c>.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(this DbContextOptionsBuilder optionsBuilder, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        return With(optionsBuilder, extension => extension.WithDatabaseName(databaseName));
    }

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, string)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, string databaseName)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, databaseName);

    /// <summary>
    /// Persists through <paramref name="storage"/>. The storage instance is the store identity:
    /// every context given the same instance shares one engine, and a later context after the
    /// engine is released recovers from what was flushed.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(this DbContextOptionsBuilder optionsBuilder, IBlazeDbStorage storage) =>
        UseBlazeDb(optionsBuilder, storage, configure: null);

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, IBlazeDbStorage)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, IBlazeDbStorage storage)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, storage);

    /// <summary>
    /// Opens BlazeDb with the given engine settings. Tables are still discovered from the context
    /// model - do not call <c>AddTable</c> here.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(
        this DbContextOptionsBuilder optionsBuilder, Action<BlazeDb.BlazeDbDatabaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configure);
        return With(optionsBuilder, extension =>
        {
            if (extension.DatabaseName is null && extension.Storage is null && extension.ExistingDatabase is null)
            {
                extension = extension.WithDatabaseName("BlazeDb-" + Guid.NewGuid().ToString("N"));
            }
            return extension.WithConfigure(configure);
        });
    }

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, Action{BlazeDb.BlazeDbDatabaseOptions})"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, Action<BlazeDb.BlazeDbDatabaseOptions> configure)
        where TContext : DbContext
    {
        UseBlazeDb(optionsBuilder, typeof(TContext).FullName ?? typeof(TContext).Name);
        return (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, configure);
    }

    /// <summary><see cref="UseBlazeDb(DbContextOptionsBuilder, IBlazeDbStorage)"/> plus engine settings.</summary>
    public static DbContextOptionsBuilder UseBlazeDb(
        this DbContextOptionsBuilder optionsBuilder,
        IBlazeDbStorage storage,
        Action<BlazeDb.BlazeDbDatabaseOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(storage);
        return With(optionsBuilder, extension =>
        {
            extension = extension.WithStorage(storage);
            return configure is null ? extension : extension.WithConfigure(configure);
        });
    }

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, IBlazeDbStorage, Action{BlazeDb.BlazeDbDatabaseOptions}?)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IBlazeDbStorage storage,
        Action<BlazeDb.BlazeDbDatabaseOptions>? configure)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, storage, configure);

    /// <summary>
    /// Runs against an already-open engine, analogous to <c>UseSqlite(connection)</c>. The
    /// application owns the engine's lifetime. Prefer <see cref="UseBlazeDb(DbContextOptionsBuilder)"/>
    /// or <see cref="UseBlazeDb(DbContextOptionsBuilder, IBlazeDbStorage)"/> when you do not already
    /// have one.
    /// </summary>
    public static DbContextOptionsBuilder UseBlazeDb(this DbContextOptionsBuilder optionsBuilder, BlazeDb.BlazeDbDatabase database)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(database);
        return With(optionsBuilder, extension => extension.WithDatabase(database));
    }

    /// <inheritdoc cref="UseBlazeDb(DbContextOptionsBuilder, BlazeDb.BlazeDbDatabase)"/>
    public static DbContextOptionsBuilder<TContext> UseBlazeDb<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder, BlazeDb.BlazeDbDatabase database)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseBlazeDb((DbContextOptionsBuilder)optionsBuilder, database);

    private static DbContextOptionsBuilder With(
        DbContextOptionsBuilder optionsBuilder, Func<BlazeDbOptionsExtension, BlazeDbOptionsExtension> with)
    {
        var extension = with(optionsBuilder.Options.FindExtension<BlazeDbOptionsExtension>() ?? new BlazeDbOptionsExtension());
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}
