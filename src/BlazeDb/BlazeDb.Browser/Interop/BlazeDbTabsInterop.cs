using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace BlazeDb.Browser;

/// <summary>Bindings to the blazedb-tabs.js ES module.</summary>
[SupportedOSPlatform("browser")]
internal static partial class BlazeDbTabsInterop
{
    private const string ModuleName = BlazeDbBrowserModules.Tabs;

    public static Task EnsureModuleAsync(string? moduleUrl) => BlazeDbBrowserModules.EnsureAsync(ModuleName, moduleUrl);

    [JSImport("open", ModuleName)]
    public static partial void Open(string channelName, [JSMarshalAs<JSType.Function<JSType.Number>>] Action<double>? onMessage);

    [JSImport("post", ModuleName)]
    public static partial void Post(string channelName, double generation);

    [JSImport("close", ModuleName)]
    public static partial void Close(string channelName);

    [JSImport("isSupported", ModuleName)]
    public static partial bool IsSupported();
}
