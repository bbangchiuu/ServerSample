using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace ServerSampleApi.Tests;

/// <summary>
/// PostgreSQL và Redis RIÊNG cho lần chạy test, do chính code test tự bật bằng Testcontainers.
///
/// Trước đây test dùng container của docker-compose (localhost:5432, localhost:6379): phải tự bật trước,
/// và test chạy chung PostgreSQL/Redis với lúc bạn chạy API. Bây giờ:
///  - Lần đầu có lớp test cần, Testcontainers tạo 2 container MỚI (postgres:17, redis:8, cùng phiên bản với
///    container dev) trên một CỔNG NGẪU NHIÊN, nên không đụng container dev, chạy test lúc đang chạy API cũng được.
///  - Chạy test xong (kể cả khi bị tắt ngang), container "ryuk" của Testcontainers tự xoá chúng: lần sau lại sạch.
/// Vẫn cần Docker Desktop đang chạy (Testcontainers điều khiển Docker), nhưng KHÔNG cần docker-compose up.
///
/// Cả lần chạy chỉ có MỘT PostgreSQL và MỘT Redis, dùng chung cho mọi lớp test: bật container mất vài giây,
/// mỗi lớp test tự bật riêng thì rất chậm. Các lớp test vẫn không đụng nhau như trước: mỗi ApiFactory có
/// database riêng (game_test_...) và prefix key Redis riêng.
/// </summary>
internal static class TestInfrastructure
{
    private static readonly PostgreSqlContainer Postgres = new PostgreSqlBuilder("postgres:17").Build();

    private static readonly RedisContainer Redis = new RedisBuilder("redis:8").Build();

    // Lazy<Task>: lớp test nào gọi EnsureStartedAsync TRƯỚC thì bật container, các lớp chạy song song gọi sau
    // nhận lại CÙNG một Task đó (chờ chung), không bật thêm container nào.
    private static readonly Lazy<Task> Started = new(() => Task.WhenAll(Postgres.StartAsync(), Redis.StartAsync()));

    /// <summary>Bật PostgreSQL và Redis (song song) nếu chưa bật. Gọi bao nhiêu lần cũng chỉ bật một lần.</summary>
    public static Task EnsureStartedAsync() => Started.Value;

    /// <summary>Chuỗi kết nối tới container PostgreSQL, nhưng trỏ vào database tên databaseName.</summary>
    public static string PostgresConnectionString(string databaseName) =>
        new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = databaseName }.ConnectionString;

    /// <summary>Chuỗi kết nối tới container Redis (dạng "127.0.0.1:cổng_ngẫu_nhiên"), giữ abortConnect=false như appsettings.</summary>
    public static string RedisConnectionString => Redis.GetConnectionString() + ",abortConnect=false";
}
