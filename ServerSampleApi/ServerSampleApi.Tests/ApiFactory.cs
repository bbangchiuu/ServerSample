using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Data;

namespace ServerSampleApi.Tests;

/// <summary>
/// "Server ảo" cho test, giống WebApplicationFactory&lt;Program&gt; nhưng dùng một DATABASE RIÊNG:
///  - PostgreSQL và Redis là container riêng của test, tự bật bằng Testcontainers (xem TestInfrastructure),
///    không phải container dev của docker-compose. Chỉ cần Docker Desktop đang chạy.
///  - Mỗi lớp test (IClassFixture) nhận một ApiFactory, tức một database tên ngẫu nhiên "game_test_..."
///    trong container PostgreSQL đó, và một prefix key riêng trong container Redis.
///  - Trước khi test chạy (InitializeAsync): bật container (nếu chưa), tạo database và chạy toàn bộ migration.
/// Nhờ vậy các lớp test chạy song song không đụng dữ liệu của nhau, và không bao giờ đụng dữ liệu dev.
/// Không cần tự xoá database/key sau khi test xong: chạy xong, cả container bị xoá.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Guid dạng "N" (32 ký tự hex liền nhau, chữ thường, không có "-"): tên gọn, gõ tay trong psql không cần ngoặc kép.
    // (Không bắt buộc: EF Core/Npgsql tự đặt tên database trong ngoặc kép khi CREATE/DROP.)
    private readonly string _databaseName = $"game_test_{Guid.NewGuid():N}";

    private string ConnectionString => TestInfrastructure.PostgresConnectionString(_databaseName);

    // Redis không có "database riêng theo tên" như PostgreSQL, nên mỗi lớp test dùng một KEY riêng
    // cho bảng xếp hạng, không đụng vào key "leaderboard:gold" thật.
    public string LeaderboardKey => $"test:{_databaseName}:leaderboard:gold";

    /// <summary>Prefix các key danh sách online của lớp test này: ...:players, ...:servers, ...:server:{id}.</summary>
    public string PresenceKeyPrefix => $"test:{_databaseName}:presence";

    /// <summary>
    /// Khoá ký JWT chỉ dùng cho test. Không lấy khoá trong User Secrets, vì User Secrets chỉ có trên máy bạn:
    /// máy khác (hoặc server CI chạy test tự động) không có thì test sẽ hỏng.
    /// </summary>
    public const string JwtSigningKey = "test-only-signing-key-0123456789-abcdefghij";

    /// <summary>Khoá hệ thống cho các API quản trị ([RequireSystemKey]), chỉ dùng cho test. Cùng lý do như JwtSigningKey.</summary>
    public const string SystemApiKey = "test-only-system-key-0123456789-abcdefghij";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ghi đè chuỗi kết nối "GameDb" và "Redis" trong appsettings.Development.json, chỉ trong server ảo của test:
        // trỏ tới container của Testcontainers (cổng ngẫu nhiên) thay vì localhost:5432 / localhost:6379.
        builder.UseSetting("ConnectionStrings:GameDb", ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", TestInfrastructure.RedisConnectionString);
        builder.UseSetting("Leaderboard:RedisKey", LeaderboardKey);
        builder.UseSetting("Presence:RedisKeyPrefix", PresenceKeyPrefix);
        // Kênh backplane riêng: Clients.All của lớp test này không gửi sang server ảo của lớp test khác.
        builder.UseSetting("SignalR:RedisChannelPrefix", $"test:{_databaseName}");

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
        // Phải bật container TRƯỚC khi đụng tới Services: lần đầu đụng tới Services là lúc server ảo được tạo
        // và ConfigureWebHost chạy, mà ConfigureWebHost cần chuỗi kết nối (cổng ngẫu nhiên) của container.
        await TestInfrastructure.EnsureStartedAsync();

        using IServiceScope scope = Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();

        // MigrateAsync: database chưa có thì TẠO, rồi chạy mọi migration (InitialCreate, ...).
        // Test luôn chạy trên đúng cấu trúc database mới nhất, giống hệt database thật.
        await db.Database.MigrateAsync();
    }

    /// <summary>
    /// xUnit gọi hàm này MỘT lần, sau test cuối cùng của lớp test: tắt server ảo.
    /// Không cần DROP DATABASE hay xoá key Redis như trước: container của test bị xoá khi chạy xong.
    /// </summary>
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
