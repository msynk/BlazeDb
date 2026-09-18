using BlazeDb.EntityFrameworkCore.Infrastructure;
using BlazeDb.EntityFrameworkCore.Metadata;
using BlazeDb.EntityFrameworkCore.Query;
using BlazeDb.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.ValueGeneration;

// See BlazeDbQueryCompiler: replacing IQueryCompiler is a deliberate dependency on an EF internal.
#pragma warning disable EF1001

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the BlazeDb provider's services.</summary>
public static class BlazeDbServiceCollectionExtensions
{
    public static IServiceCollection AddEntityFrameworkBlazeDb(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        // No query-translation services are registered. The provider replaces IQueryCompiler
        // instead, so EF's translation and materialization pipeline is never built: rows are
        // already objects in memory and need no shaping.
        new EntityFrameworkServicesBuilder(serviceCollection)
            .TryAdd<LoggingDefinitions, BlazeDbLoggingDefinitions>()
            .TryAdd<IDatabaseProvider, DatabaseProvider<BlazeDbOptionsExtension>>()
            .TryAdd<IDatabase, BlazeDbEfCoreDatabase>()
            .TryAdd<IDbContextTransactionManager, BlazeDbTransactionManager>()
            .TryAdd<IDatabaseCreator, BlazeDbDatabaseCreator>()
            .TryAdd<IQueryContextFactory, BlazeDbQueryContextFactory>()
            .TryAdd<IQueryCompiler, BlazeDbQueryCompiler>()
            .TryAdd<IProviderConventionSetBuilder, BlazeDbConventionSetBuilder>()
            .TryAdd<ITypeMappingSource, BlazeDbTypeMappingSource>()
            .TryAdd<IValueGeneratorSelector, ValueGeneratorSelector>()
            .TryAddProviderSpecificServices(b =>
            {
                b.TryAddSingleton<BlazeDbEngineCache, BlazeDbEngineCache>();
                b.TryAddScoped<IBlazeDbTableCache, BlazeDbTableCache>();
            })
            .TryAddCoreServices();

        return serviceCollection;
    }
}
