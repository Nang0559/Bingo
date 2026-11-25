using Bingo;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<CardService>();

var host = builder.Build();

// Nạp thiết lập đã lưu trước khi hiển thị giao diện
await host.Services.GetRequiredService<SettingsService>().LoadAsync();
// Nạp bộ thẻ đã phát hành (nếu có) và đồng bộ với thiết lập
await host.Services.GetRequiredService<CardService>().LoadAsync();

await host.RunAsync();
