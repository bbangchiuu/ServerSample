using Microsoft.AspNetCore.Identity;
using Serilog;
using Serilog.Events;
using ServerSample.Api.Accounts;
using ServerSample.Api.Data;
using ServerSample.Api.Players;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Cổng của HTTP API. TCP server (ServerSample.Server) vẫn chạy riêng ở cổng 7777.
builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddSerilog(logger => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Mọi lỗi trả về theo cùng một định dạng chuẩn "ProblemDetails" (RFC 9457):
// { "type": "...", "title": "...", "status": 409, "detail": "...", "traceId": "..." }
builder.Services.AddProblemDetails();

// Sinh tài liệu mô tả toàn bộ API (OpenAPI) tại /openapi/v1.json
builder.Services.AddOpenApi();

// Singleton: cả server dùng chung một "database" trong RAM. Giai đoạn 3 thay bằng PostgreSQL.
builder.Services.AddSingleton<InMemoryDatabase>();
// Băm mật khẩu bằng PBKDF2 (có sẵn trong ASP.NET Core, không cần cài thêm package).
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
// Lấy giờ qua TimeProvider thay vì DateTime.UtcNow, để test có thể giả lập "sang ngày mới".
builder.Services.AddSingleton(TimeProvider.System);

WebApplication app = builder.Build();

// ===== Middleware pipeline: mỗi request đi qua các bước dưới đây theo đúng thứ tự từ trên xuống =====
app.UseSerilogRequestLogging(); // log 1 dòng mỗi request: method, đường dẫn, status code, thời gian xử lý
app.UseExceptionHandler();      // exception chưa ai bắt → trả 500 dạng ProblemDetails, không lộ stack trace
app.UseStatusCodePages();       // lỗi không có body (404, 405...) → tự thêm body ProblemDetails

// Tài liệu API và giao diện thử API trên trình duyệt: http://localhost:5080/swagger
// Đang học nên bật luôn; khi deploy thật (giai đoạn 7) chỉ nên bật ở môi trường Development.
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));

app.MapAccountEndpoints();
app.MapPlayerEndpoints();

app.Run();

// Để project test tạo được server ảo từ chính Program này (WebApplicationFactory<Program>).
public partial class Program
{
}
