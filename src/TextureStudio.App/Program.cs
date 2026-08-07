using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TextureStudio.App;
using TextureStudio.App.Services;
using TextureStudio.Core.Generation;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
});
builder.Services.AddFluentUIComponents();
builder.Services.AddScoped<GeminiImageClient>();
builder.Services.AddScoped<ImageCodec>();
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddScoped<StudioState>();

await builder.Build().RunAsync();
