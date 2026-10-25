using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using ServerSample.Server;
using ServerSample.Server.Networking;

// Generic Host = "bộ khung" chuẩn của .NET: Dependency Injection, logging,
// và xử lý tắt server êm (Ctrl+C / SIGTERM của Docker).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Log ghi ra 2 nơi: console và file.
builder.Services.AddSerilog(logger => logger
    // Chỉ ghi từ mức Information trở lên (bỏ Debug/Verbose).
    .MinimumLevel.Information()
    // Log nội bộ của .NET (namespace Microsoft.*) chỉ ghi khi có cảnh báo/lỗi, cho console đỡ rối.
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        // Luôn nằm trong <thư mục .dll>/logs, bất kể chạy lệnh từ thư mục nào.
        path: Path.Combine(AppContext.BaseDirectory, "logs", "server-.log"),
        rollingInterval: RollingInterval.Day,  // mỗi ngày một file: server-20261025.log
        retainedFileCountLimit: 14,            // giữ 14 file gần nhất, tự xóa file cũ hơn
        fileSizeLimitBytes: 100 * 1024 * 1024, // tối đa 100MB mỗi file...
        rollOnFileSizeLimit: true,             // ...đầy thì tách sang file mới thay vì ngừng ghi
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Cấu hình server: giá trị nằm trong ServerOptions.cs.
builder.Services
    .AddOptions<ServerOptions>()
    .ValidateDataAnnotations()
    .ValidateOnStart(); // giá trị vi phạm [Range] thì báo lỗi ngay khi khởi động

// Singleton: cả server dùng chung một instance.
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<PacketDispatcher>();

builder.Services.AddHostedService<TcpServerService>();
builder.Services.AddHostedService<HeartbeatMonitor>();

await builder.Build().RunAsync();
