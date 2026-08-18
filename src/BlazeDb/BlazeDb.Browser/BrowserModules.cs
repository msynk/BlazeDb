using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace BlazeDb.Browser;

/// <summary>
/// Loads BlazeDb's JS modules once each and resolves their URLs against the document base.
/// Relative URLs passed to <see cref="JSHost.ImportAsync"/> resolve against the runtime's
/// <c>_framework/</c> directory rather than the app base, which is why the base URI is applied here.
/// </summary>
[SupportedOSPlatform("browser")]
internal static class BrowserModules
{
    public const string Opfs = "blazedb-opfs";
    public const string Crypto = "blazedb-crypto";
    public const string IndexedDb = "blazedb-idb";
    public const string Tabs = "blazedb-tabs";

    private static readonly ConcurrentDictionary<string, Task> Imports = new();

    public static Task EnsureAsync(string moduleName, string? moduleUrl = null)
    {
        // A faulted import must not stay cached, otherwise every later attempt replays the failure.
        if (Imports.TryGetValue(moduleName, out var existing) && !existing.IsFaulted)
        {
            return existing;
        }
        var import = JSHost.ImportAsync(moduleName, moduleUrl ?? ResolveUrl(moduleName));
        Imports[moduleName] = import;
        return import;
    }

    private static string ResolveUrl(string moduleName)
    {
        var path = $"_content/BlazeDb.Browser/{moduleName}.js";
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document");
        var baseUri = document?.GetPropertyAsString("baseURI");
        return string.IsNullOrEmpty(baseUri) ? "/" + path : baseUri + path;
    }
}
