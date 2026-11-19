using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test health check (giai đoạn 7, bước 2): /health/live và /health/ready, khi mọi thứ ổn,
/// khi PostgreSQL sập (503) và khi Redis sập (vẫn 200 nhưng "Degraded").
/// </summary>
public class HealthCheckTests : IClassFixture<ApiFactory>
{
    private const string DatabaseDown = "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=wrong;Timeout=3";
    private const string RedisDown = "127.0.0.1:1,abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000";

    private readonly ApiFactory _factory;

    public HealthCheckTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Live_Returns200_WithoutToken()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await ReadJsonAsync(response);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        // live không kiểm tra gì bên ngoài.
        Assert.Empty(body.GetProperty("checks").EnumerateObject());
    }

    [Fact]
    public async Task Ready_EverythingUp_Returns200Healthy()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await ReadJsonAsync(response);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", CheckStatus(body, "postgres"));
        Assert.Equal("Healthy", CheckStatus(body, "redis"));
    }

    [Fact]
    public async Task Ready_DatabaseDown_Returns503Unhealthy_ButLiveStillReturns200()
    {
        await using WebApplicationFactory<Program> server = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:GameDb", DatabaseDown));
        HttpClient client = server.CreateClient();

        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        JsonElement body = await ReadJsonAsync(ready);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", CheckStatus(body, "postgres"));
        Assert.Equal("Healthy", CheckStatus(body, "redis"));
        Assert.DoesNotContain("127.0.0.1", body.GetRawText()); // không lộ địa chỉ database

        // Process vẫn sống: Docker KHÔNG nên khởi động lại (khởi động lại cũng không làm database sống lại).
        HttpResponseMessage live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Ready_RedisDown_Returns200Degraded()
    {
        await using WebApplicationFactory<Program> server = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Redis", RedisDown));

        HttpResponseMessage ready = await server.CreateClient().GetAsync("/health/ready");

        // Vẫn 200: phần chính (đăng nhập, profile, nhận thưởng) vẫn chạy, chỉ báo là "hoạt động hạn chế".
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        JsonElement body = await ReadJsonAsync(ready);
        Assert.Equal("Degraded", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", CheckStatus(body, "postgres"));
        Assert.Equal("Degraded", CheckStatus(body, "redis"));
    }

    [Fact]
    public async Task HealthRequests_AreNotWrittenByHttpLogging_ButOtherRequestsAre()
    {
        var logs = new CapturingLoggerProvider();
        await using WebApplicationFactory<Program> server = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        HttpClient client = server.CreateClient();

        await client.GetAsync("/health/live");
        await client.GetAsync("/health/ready");
        await client.GetAsync("/api/leaderboard?count=1"); // request thường: phải có log

        await logs.WaitForAsync(l => IsHttpLog(l) && l.Values.GetValueOrDefault("Path")?.ToString() == "/api/leaderboard");
        Assert.DoesNotContain(logs.Logs, l => IsHttpLog(l) && l.Values.GetValueOrDefault("Path")?.ToString()?.StartsWith("/health") == true);
    }

    private static bool IsHttpLog(CapturedLog log) => log.Category == "Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware";

    private static string? CheckStatus(JsonElement body, string checkName) =>
        body.GetProperty("checks").GetProperty(checkName).GetProperty("status").GetString();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
}
