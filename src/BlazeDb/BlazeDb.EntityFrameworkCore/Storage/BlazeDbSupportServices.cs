using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BlazeDb.EntityFrameworkCore.Storage;

/// <summary>
/// The engine keeps every row as a live .NET object, so there is nothing to convert between a
/// store type and a CLR type. Every type the model can express is mapped as itself.
/// </summary>
public sealed class BlazeDbTypeMappingSource : TypeMappingSource
{
    public BlazeDbTypeMappingSource(TypeMappingSourceDependencies dependencies) : base(dependencies)
    {
    }

    protected override CoreTypeMapping? FindMapping(in TypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        return clrType is null ? null : new BlazeDbTypeMapping(clrType);
    }

    private sealed class BlazeDbTypeMapping : CoreTypeMapping
    {
        public BlazeDbTypeMapping(Type clrType) : base(new CoreTypeMappingParameters(clrType))
        {
        }

        private BlazeDbTypeMapping(CoreTypeMappingParameters parameters) : base(parameters)
        {
        }

        public override CoreTypeMapping WithComposedConverter(
            ValueConverter? converter,
            ValueComparer? comparer = null,
            ValueComparer? keyComparer = null,
            CoreTypeMapping? elementMapping = null,
            JsonValueReaderWriter? jsonValueReaderWriter = null) =>
            new BlazeDbTypeMapping(Parameters.WithComposedConverter(
                converter, comparer, keyComparer, elementMapping, jsonValueReaderWriter));

        protected override CoreTypeMapping Clone(CoreTypeMappingParameters parameters) =>
            new BlazeDbTypeMapping(parameters);
    }
}

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

/// <summary>
/// BlazeDb has one ambient transaction at a time, opened at <c>SaveChanges</c>. An explicit
/// <c>BeginTransaction</c> spanning several saves is not supported, matching the engine's
/// single-writer, no-nesting model.
/// </summary>
public sealed class BlazeDbTransactionManager : IDbContextTransactionManager
{
    public IDbContextTransaction? CurrentTransaction => null;

    public IDbContextTransaction BeginTransaction() => throw NotSupported();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw NotSupported();

    public void CommitTransaction() => throw NotSupported();

    public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => throw NotSupported();

    public void RollbackTransaction() => throw NotSupported();

    public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => throw NotSupported();

    public void ResetState()
    {
    }

    public Task ResetStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static NotSupportedException NotSupported() =>
        new("BlazeDb does not support transactions spanning several SaveChanges calls. " +
            "Each SaveChanges is already atomic: it commits as one engine transaction and one WAL record.");
}

/// <summary>Model conventions, minus every relational one.</summary>
public sealed class BlazeDbConventionSetBuilder : ProviderConventionSetBuilder
{
    public BlazeDbConventionSetBuilder(ProviderConventionSetBuilderDependencies dependencies) : base(dependencies)
    {
    }

    public override ConventionSet CreateConventionSet()
    {
        var conventionSet = base.CreateConventionSet();
        conventionSet.Add(new BlazeDbAttributeConvention());
        conventionSet.Add(new NoValueGenerationConvention());
        return conventionSet;
    }

    /// <summary>
    /// Reads the engine's own attributes into the model, so a table type needs no second set of
    /// EF annotations: <c>[BlazeDb.Key]</c> names the primary key - which EF's conventions would
    /// otherwise only find when it happens to be called <c>Id</c> - and <c>[BlazeDb.Ignore]</c>
    /// keeps a property out of the entity type, matching what the serializer does with it.
    /// </summary>
    private sealed class BlazeDbAttributeConvention : IEntityTypeAddedConvention
    {
        public void ProcessEntityTypeAdded(
            IConventionEntityTypeBuilder entityTypeBuilder, IConventionContext<IConventionEntityTypeBuilder> context)
        {
            var clrType = entityTypeBuilder.Metadata.ClrType;
            foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.IsDefined(typeof(IgnoreAttribute), inherit: true))
                {
                    entityTypeBuilder.Ignore(property.Name, fromDataAnnotation: true);
                    continue;
                }
                if (property.IsDefined(typeof(KeyAttribute), inherit: true))
                {
                    var propertyBuilder = entityTypeBuilder.Property(property, fromDataAnnotation: true);
                    if (propertyBuilder is not null)
                    {
                        entityTypeBuilder.PrimaryKey([propertyBuilder.Metadata], fromDataAnnotation: true);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Turns off value generation across the model. The engine assigns nothing: a row's key is
    /// whatever the application put in it, which is also what makes a row's identity stable enough
    /// to hand the live object straight back from a query.
    /// </summary>
    private sealed class NoValueGenerationConvention : IModelFinalizingConvention
    {
        public void ProcessModelFinalizing(
            IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
        {
            foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    property.Builder.ValueGenerated(ValueGenerated.Never);
                }
            }
        }
    }
}
