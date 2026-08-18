using System.Runtime.Versioning;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using BlazeDb.Demo;
using BlazeDb.Demo.Components;
using BlazeDb.Demo.Data;

[assembly: SupportedOSPlatform("browser")]

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<DemoDbService>();
builder.Services.AddSingleton<AppInterop>();

await builder.Build().RunAsync();
