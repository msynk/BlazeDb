using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace BlazeDb.Browser;

/// <summary>Bindings to the blazedb-tabs.js ES module.</summary>
[SupportedOSPlatform("browser")]
internal static partial class TabsInterop
{
    private const string ModuleName = BrowserModules.Tabs;

    public static Task EnsureModuleAsync(string? moduleUrl) => BrowserModules.EnsureAsync(ModuleName, moduleUrl);

    [JSImport("open", ModuleName)]
    public static partial void Open(string channelName, [JSMarshalAs<JSType.Function<JSType.Number>>] Action<double>? onMessage);

    [JSImport("post", ModuleName)]
    public static partial void Post(string channelName, double generation);

    [JSImport("close", ModuleName)]
    public static partial void Close(string channelName);

    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();
}

/// <summary>
/// Keeps read-only replica tabs in step with the writer tab. The writer announces each checkpoint
/// on a BroadcastChannel; replicas respond by reloading from storage.
/// <para>
/// Only the signal crosses tabs — never the data. Replicas re-read the snapshot and WAL the writer
/// has already made durable, so they can never observe a state the writer has not committed. The
/// consequence is that a replica trails the writer by up to one checkpoint; call
/// <see cref="Database.ReloadAsync"/> directly if a tab needs to catch up sooner.
/// </para>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class TabSync : IAsyncDisposable
{
    private readonly string _channelName;
    private readonly Database? _replica;
    private readonly Func<ValueTask>? _onChanged;
    private bool _disposed;

    private TabSync(string channelName, Database? replica, Func<ValueTask>? onChanged)
    {
        _channelName = channelName;
        _replica = replica;
        _onChanged = onChanged;
    }

    /// <summary>Whether this browser implements BroadcastChannel.</summary>
    public static async ValueTask<bool> IsSupportedAsync(string? moduleUrl = null)
    {
        await TabsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        return TabsInterop.IsSupported();
    }

    /// <summary>
    /// Announces this tab's checkpoints to replicas. Wire the returned publisher up before opening
    /// the database by assigning <see cref="PublishCheckpoint"/> to
    /// <see cref="DatabaseOptions.OnCheckpoint"/>.
    /// </summary>
    public static async ValueTask<TabSync> CreatePublisherAsync(string databaseName, string? moduleUrl = null)
    {
        await TabsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);
        var channelName = ChannelName(databaseName);
        TabsInterop.Open(channelName, null);
        return new TabSync(channelName, replica: null, onChanged: null);
    }

    /// <summary>
    /// Subscribes <paramref name="replica"/> to the writer tab's checkpoints, reloading it each
    /// time one lands. <paramref name="onChanged"/> runs after a successful reload — use it to
    /// re-run queries and refresh the UI.
    /// </summary>
    public static async ValueTask<TabSync> CreateReplicaAsync(
        string databaseName,
        Database replica,
        Func<ValueTask>? onChanged = null,
        string? moduleUrl = null)
    {
        ArgumentNullException.ThrowIfNull(replica);
        await TabsInterop.EnsureModuleAsync(moduleUrl).ConfigureAwait(false);

        var channelName = ChannelName(databaseName);
        var sync = new TabSync(channelName, replica, onChanged);
        TabsInterop.Open(channelName, generation =>
        {
            // Fire and forget: the JS callback cannot await, and a failed reload is retried on the
            // next checkpoint anyway.
            _ = sync.OnCheckpointAnnouncedAsync();
        });
        return sync;
    }

    /// <summary>
    /// Hand this to <see cref="DatabaseOptions.OnCheckpoint"/> on the writer tab.
    /// </summary>
    public Action<ulong> PublishCheckpoint => generation => TabsInterop.Post(_channelName, generation);

    /// <summary>Reloads the replica now, without waiting for the next checkpoint.</summary>
    public async ValueTask RefreshAsync()
    {
        if (_replica is null || _disposed)
        {
            return;
        }
        await _replica.ReloadAsync().ConfigureAwait(false);
        if (_onChanged is not null)
        {
            await _onChanged().ConfigureAwait(false);
        }
    }

    private async Task OnCheckpointAnnouncedAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The tab tore the database down between the announcement and the reload.
        }
    }

    private static string ChannelName(string databaseName) => "blazedb:" + databaseName;

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            TabsInterop.Close(_channelName);
        }
        return default;
    }
}
