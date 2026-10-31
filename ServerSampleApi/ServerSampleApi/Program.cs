using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Models;
using ServerSampleApi.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

// Băm mật khẩu (thuật toán PBKDF2), có sẵn trong ASP.NET Core, không cần cài thêm package.
builder.Services.AddSingleton<IPasswordHasher<AccountModel>, PasswordHasher<AccountModel>>();

// Database PostgreSQL qua EF Core. Chuỗi kết nối "GameDb" nằm trong appsettings.Development.json.
// AddDbContext đăng ký kiểu Scoped: MỖI REQUEST một GameDbContext riêng, xong request thì bỏ đi.
builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("GameDb")));

// Redis. Chuỗi kết nối "Redis" nằm trong appsettings.Development.json: "localhost:6379,abortConnect=false".
//  - ConnectionMultiplexer giữ kết nối tới Redis, tự kết nối lại khi bị rớt. Tạo MỘT lần (Singleton) và dùng chung.
//  - abortConnect=false: lúc server khởi động mà Redis chưa bật thì server VẪN chạy, thư viện tự thử kết nối lại
//    ở phía sau. Thiếu nó thì Connect() ném lỗi và cả API không khởi động được chỉ vì thiếu bảng xếp hạng.
// Dùng dạng "factory" (sp => ...) để chỉ kết nối khi có nơi cần dùng lần đầu, lúc đó cấu hình đã đầy đủ
// (kể cả phần test ghi đè).
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!));
// Scoped (không phải Singleton) vì LeaderboardService dùng GameDbContext, mà GameDbContext là Scoped.
builder.Services.AddScoped<LeaderboardService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Redis chưa có bảng xếp hạng thì dựng lại từ PostgreSQL, TRƯỚC khi server nhận request.
// Ngoài HTTP request thì không có scope sẵn, nên tự tạo một scope để lấy LeaderboardService (Scoped).
// Test tắt bước này (Leaderboard:RebuildOnStartup = false) vì lúc server ảo khởi động,
// database test chưa chạy migration, chưa có bảng Players.
if (app.Configuration.GetValue("Leaderboard:RebuildOnStartup", defaultValue: true))
{
    using IServiceScope scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LeaderboardService>().RebuildIfMissingAsync();
}

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); 
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "ServerSample API"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
