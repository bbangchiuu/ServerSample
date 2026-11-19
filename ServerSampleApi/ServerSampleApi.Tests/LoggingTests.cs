using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test logging (giai đoạn 7, bước 1): mỗi request một dòng log có PlayerId; log trong request mang PlayerId;
/// và KHÔNG BAO GIỜ ghi token ra log.
/// </summary>
public class LoggingTests : IClassFixture<ApiFactory>
{
    private const string HttpLoggingCategory = "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware";
    private const string SqlLoggingCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    private readonly ApiFactory _factory;

    public LoggingTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task EachRequest_IsLoggedOnce_WithStatusDurationAndPlayerId()
    {
        (CapturingLoggerProvider logs, HttpClient client, Guid playerId, _) = await StartServerAndLoginAsync();

        HttpResponseMessage response = await client.GetAsync("/api/players/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        CapturedLog requestLog = await logs.WaitForAsync(l => IsRequestLog(l, "/api/players/me"));
        Assert.Equal("GET", requestLog.Values["Method"]?.ToString());
        Assert.Equal("200", requestLog.Values["StatusCode"]?.ToString());
        Assert.True(requestLog.Values.ContainsKey("Duration")); // thời gian xử lý (mili giây)
        Assert.Equal(playerId.ToString(), requestLog.Values["PlayerId"]?.ToString());

        // CombineLogs: request và response gộp thành ĐÚNG MỘT dòng.
        Assert.Single(logs.Logs, l => IsRequestLog(l, "/api/players/me"));
    }

    // Giá trị "Path" trong log là kiểu PathString (không phải string): khi ghi ra JSON thì thành chuỗi,
    // nhưng trong bộ nhớ thì phải ToString() mới so sánh với chuỗi được.
    private static bool IsRequestLog(CapturedLog log, string path) =>
        log.Category == HttpLoggingCategory && log.Values.GetValueOrDefault("Path")?.ToString() == path;

    [Fact]
    public async Task LogsWrittenDuringRequest_CarryPlayerId()
    {
        (CapturingLoggerProvider logs, HttpClient client, Guid playerId, _) = await StartServerAndLoginAsync();

        await client.GetAsync("/api/players/me");

        // Câu SQL do EF Core ghi trong lúc xử lý request (một class khác hẳn, không biết gì về PlayerId)
        // vẫn mang PlayerId nhờ scope do PlayerIdLogScopeMiddleware mở.
        await logs.WaitForAsync(l =>
            l.Category == SqlLoggingCategory && l.Values.GetValueOrDefault("PlayerId")?.ToString() == playerId.ToString());
    }

    [Fact]
    public async Task TokensAreNeverWrittenToLogs()
    {
        (CapturingLoggerProvider logs, HttpClient client, _, string accessToken) = await StartServerAndLoginAsync();

        // Token trong header Authorization VÀ trong query string (SignalR gửi token theo cách này).
        const string queryToken = "SECRET-QUERY-TOKEN";
        await client.GetAsync($"/api/players/me?access_token={queryToken}");
        await logs.WaitForAsync(l => l.Category == HttpLoggingCategory && l.Values.ContainsKey("PlayerId"));

        foreach (CapturedLog log in logs.Logs)
        {
            string everything = log.Message + " " + string.Join(" ", log.Values.Values);
            Assert.DoesNotContain(accessToken, everything);
            Assert.DoesNotContain(queryToken, everything);
        }
    }

    [Fact]
    public async Task UnhandledError_ErrorLogCarriesPlayerId()
    {
        // Server có database "sập" (cổng 1 không có gì). Token tự ký đúng khoá nên vẫn hợp lệ, không cần database.
        var logs = new CapturingLoggerProvider();
        await using WebApplicationFactory<Program> databaseDown = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:GameDb",
                "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=wrong;Timeout=3");
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        });
        HttpClient client = databaseDown.CreateClient();
        Guid playerId = Guid.NewGuid();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestHelper.CreateToken(playerId, DateTime.UtcNow.AddMinutes(10)));

        HttpResponseMessage response = await client.GetAsync("/api/players/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Dòng Error quan trọng nhất (do GlobalExceptionHandler ghi) phải cho biết lỗi xảy ra với người chơi nào.
        // Không thể trông vào scope của PlayerIdLogScopeMiddleware: UseExceptionHandler đứng NGOÀI middleware đó,
        // nên lúc exception bay tới đây thì "using" đã đóng scope rồi.
        CapturedLog errorLog = await logs.WaitForAsync(l =>
            l.Category == "ServerSampleApi.Errors.GlobalExceptionHandler" && l.Level == LogLevel.Error);
        Assert.Equal(playerId.ToString(), errorLog.Values.GetValueOrDefault("PlayerId")?.ToString());
    }

    /// <summary>Server ảo có gắn bộ bắt log, cộng một người chơi vừa đăng ký (client đã gắn token).</summary>
    private async Task<(CapturingLoggerProvider Logs, HttpClient Client, Guid PlayerId, string AccessToken)> StartServerAndLoginAsync()
    {
        var logs = new CapturingLoggerProvider();
        WebApplicationFactory<Program> server = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        HttpClient client = server.CreateClient();

        HttpResponseMessage registered = await client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = TestHelper.NewUsername(), Password = TestHelper.Password });
        registered.EnsureSuccessStatusCode();
        AccountResponse account = (await registered.Content.ReadFromJsonAsync<AccountResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        return (logs, client, account.PlayerId, account.AccessToken);
    }
}
