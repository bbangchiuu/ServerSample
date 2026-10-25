using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using ServerSample.Server;
using ServerSample.Server.Networking;

// Generic Host = "bộ khung" chuẩn của .NET: cấu hình (appsettings.json + biến môi trường),
// Dependency Injection, logging, và xử lý tắt server êm (Ctrl+C / SIGTERM của Docker).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // Đọc appsettings.json cạnh file .dll, bất kể chạy lệnh từ thư mục nào.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddSerilog(logger => logger.ReadFrom.Configuration(builder.Configuration));

builder.Services
    .AddOptions<ServerOptions>()
    .Bind(
        builder.Configuration.GetRequiredSection(ServerOptions.SectionName), // thiếu section "Server" → lỗi
        binder => binder.ErrorOnUnknownConfiguration = true)                 // key gõ sai (vd "Prot") → lỗi
    .ValidateDataAnnotations()
    .ValidateOnStart(); // cấu hình sai thì báo lỗi ngay khi khởi động, không phải lúc đang chạy

// Singleton: cả server dùng chung một instance.
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<PacketDispatcher>();

builder.Services.AddHostedService<TcpServerService>();
builder.Services.AddHostedService<HeartbeatMonitor>();

await builder.Build().RunAsync();
