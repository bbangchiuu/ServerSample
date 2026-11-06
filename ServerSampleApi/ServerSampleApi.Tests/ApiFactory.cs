using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Data;
using StackExchange.Redis;

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
/// Cần container PostgreSQL (localhost:5432) và Redis (localhost:6379) đang chạy.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Tên database phải là chữ thường, không có dấu "-": dùng Guid dạng "N" (32 ký tự hex liền nhau).
    private readonly string _databaseName = $"game_test_{Guid.NewGuid():N}";

    private string ConnectionString =>
        $"Host=localhost;Port=5432;Database={_databaseName};Username=game;Password=game_dev_password";

    // Redis không có "database riêng theo tên" như PostgreSQL, nên mỗi lớp test dùng một KEY riêng
    // cho bảng xếp hạng, không đụng vào key "leaderboard:gold" thật.
    public string LeaderboardKey => $"test:{_databaseName}:leaderboard:gold";

    /// <summary>
    /// Khoá ký JWT chỉ dùng cho test. Không lấy khoá trong User Secrets, vì User Secrets chỉ có trên máy bạn:
    /// máy khác (hoặc server CI chạy test tự động) không có thì test sẽ hỏng.
    /// </summary>
    public const string JwtSigningKey = "test-only-signing-key-0123456789-abcdefghij";

    /// <summary>Khoá hệ thống cho các API quản trị ([RequireSystemKey]), chỉ dùng cho test. Cùng lý do như JwtSigningKey.</summary>
    public const string SystemApiKey = "test-only-system-key-0123456789-abcdefghij";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ghi đè chuỗi kết nối "GameDb" trong appsettings.Development.json, chỉ trong server ảo của test.
        builder.UseSetting("ConnectionStrings:GameDb", ConnectionString);
        builder.UseSetting("Leaderboard:RedisKey", LeaderboardKey);

        // Không tự dựng lại bảng xếp hạng lúc khởi động: khi đó database test chưa chạy migration.
        builder.UseSetting("Leaderboard:RebuildOnStartup", "false");
        builder.UseSetting("RefreshTokens:CleanupEnabled", "false");

        builder.UseSetting("Jwt:SigningKey", JwtSigningKey);
        builder.UseSetting("System:ApiKey", SystemApiKey);

        // Mọi request trong test đều đến từ cùng một "máy", nên chung một bộ đếm giới hạn. Mỗi lớp test đăng ký
        // hàng chục tài khoản, nên nới rộng giới hạn ra. RateLimitApiTests mới đặt giới hạn thấp để thử 429.
        builder.UseSetting("RateLimiting:LoginPermitLimit", "10000");
        builder.UseSetting("RateLimiting:RegisterPermitLimit", "10000");
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

            IConnectionMultiplexer redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            await redis.GetDatabase().KeyDeleteAsync(LeaderboardKey); // DEL test:...:leaderboard:gold
        }

        await base.DisposeAsync();
    }
}
