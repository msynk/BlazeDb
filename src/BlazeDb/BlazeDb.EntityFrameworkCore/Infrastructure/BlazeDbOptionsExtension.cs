using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace BlazeDb.EntityFrameworkCore.Infrastructure;

/// <summary>
/// Carries the engine instance the context runs against. BlazeDb databases are opened and owned by
/// the application — one per origin, holding the writer lock — so the provider is handed a live
/// <see cref="Database"/> rather than a connection string it would open itself.
/// </summary>
public sealed class BlazeDbOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public BlazeDbOptionsExtension()
    {
    }

    private BlazeDbOptionsExtension(BlazeDbOptionsExtension copyFrom) => Database = copyFrom.Database;

    public Database? Database { get; private set; }

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public BlazeDbOptionsExtension WithDatabase(Database database)
    {
        var clone = new BlazeDbOptionsExtension(this) { Database = database };
        return clone;
    }

    public void ApplyServices(IServiceCollection services) => services.AddEntityFrameworkBlazeDb();

    public void Validate(IDbContextOptions options)
    {
        if (Database is null)
        {
            throw new InvalidOperationException(
                "No BlazeDb database was configured. Call optionsBuilder.UseBlazeDb(database) with " +
                "an open BlazeDb.Database instance.");
        }
    }

    private sealed class ExtensionInfo : DbContextOptionsExtensionInfo
    {
        public ExtensionInfo(IDbContextOptionsExtension extension) : base(extension)
        {
        }

        private new BlazeDbOptionsExtension Extension => (BlazeDbOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => true;

        public override string LogFragment => "using BlazeDb ";

        // Which database a context talks to is not part of the service graph — the provider reads
        // it from the options per context — so every BlazeDb context can share one service
        // provider. Keying on the database instance instead would build a new one per database and
        // trip EF's "too many service providers" guard in any app that opens more than a few.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["BlazeDb:Database"] = (Extension.Database?.GetHashCode() ?? 0).ToString();
    }
}
