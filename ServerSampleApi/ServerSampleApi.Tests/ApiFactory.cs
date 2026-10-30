using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Data;

namespace ServerSampleApi.Tests;

/// <summary>
/// "Server ảo" cho test, giống WebApplicationFactory&lt;Program&gt; nhưng dùng một DATABASE RIÊNG:
///  - Mỗi lớp test (IClassFixture) nhận một ApiFactory, tức một database tên ngẫu nhiên "game_test_...",
///    nằm trong cùng container PostgreSQL với database "game".
///  - Trước khi test chạy (InitializeAsync): tạo database đó và chạy toàn bộ migration.
///  - Sau khi test xong (DisposeAsync): xoá hẳn database đó.
/// Nhờ vậy test không bao giờ thêm tài khoản thử vào database "game" bạn dùng khi chạy API,
/// và các lớp test chạy song song không đụng dữ liệu của nhau.
///
/// Cần container PostgreSQL đang chạy (localhost:5432).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Tên database phải là chữ thường, không có dấu "-": dùng Guid dạng "N" (32 ký tự hex liền nhau).
    private readonly string _databaseName = $"game_test_{Guid.NewGuid():N}";

    private string ConnectionString =>
        $"Host=localhost;Port=5432;Database={_databaseName};Username=game;Password=game_dev_password";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ghi đè chuỗi kết nối "GameDb" trong appsettings.Development.json, chỉ trong server ảo của test.
        builder.UseSetting("ConnectionStrings:GameDb", ConnectionString);
    }

    /// <summary>xUnit gọi hàm này MỘT lần, trước test đầu tiên của lớp test.</summary>
    public async Task InitializeAsync()
    {
        using IServiceScope scope = Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        // MigrateAsync: database chưa có thì TẠO, rồi chạy mọi migration (InitialCreate, ...).
        // Test luôn chạy trên đúng cấu trúc database mới nhất, giống hệt database thật.
        await db.Database.MigrateAsync();
    }

    /// <summary>xUnit gọi hàm này MỘT lần, sau test cuối cùng của lớp test.</summary>
    async Task IAsyncLifetime.DisposeAsync()
    {
        using (IServiceScope scope = Services.CreateScope())
        {
            GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            await db.Database.EnsureDeletedAsync(); // DROP DATABASE game_test_...
        }

        await base.DisposeAsync();
    }
}
