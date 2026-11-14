using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test xử lý lỗi chung (GlobalExceptionHandler, UseStatusCodePages): lỗi bất ngờ (503), sai đường dẫn (404),
/// thiếu token (401) đều trả về ProblemDetails có traceId, và lỗi bất ngờ không để lộ chi tiết bên trong server.
/// (Riêng 429 của rate limiter tự ghi ProblemDetails trong OnRejected nên chưa có traceId.)
/// </summary>
public class ErrorHandlingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ErrorHandlingTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DatabaseDown_Returns503ProblemDetails_WithTraceId_AndNoInternalDetails()
    {
        // Server thứ hai trỏ tới một PostgreSQL không tồn tại (cổng 1 không có gì), chạy như server thật (Production).
        await using WebApplicationFactory<Program> brokenServer = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            // Production không đọc appsettings.Development.json (nơi có chuỗi kết nối Redis), nên phải tự đưa vào.
            builder.UseSetting("ConnectionStrings:Redis", "localhost:6379,abortConnect=false");
            builder.UseSetting("ConnectionStrings:GameDb",
                "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=wrong;Timeout=3");
        });
        HttpClient client = brokenServer.CreateClient();

        // Đăng nhập phải đọc database: Npgsql không kết nối được và ném exception, không ai bắt trong controller.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts/login",
            new LoginRequest { Username = "someone", Password = TestHelper.Password });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        JsonElement problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.Equal("Service temporarily unavailable", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));

        // Không lộ gì bên trong: tên thư viện, địa chỉ database, stack trace.
        Assert.DoesNotContain("Npgsql", body);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain(" at ", body);
    }

    [Fact]
    public async Task RedisDown_WritesStillSucceed_BecauseRealtimeNotificationsAreBestEffort()
    {
        // Đăng ký trên server bình thường, rồi dùng chính token đó trên một server có Redis "sập".
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Server thứ hai: cùng database test, nhưng Redis trỏ tới cổng 1 (không có gì). Thời gian chờ ngắn cho test nhanh.
        // Cả bảng xếp hạng, danh sách online VÀ backplane SignalR đều dùng chuỗi kết nối này.
        await using WebApplicationFactory<Program> redisDown = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Redis",
                "127.0.0.1:1,abortConnect=false,connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000"));
        HttpClient client = redisDown.CreateClient();
        client.DefaultRequestHeaders.Authorization = player.Client.DefaultRequestHeaders.Authorization;

        // Mỗi API dưới đây LƯU vào PostgreSQL rồi mới báo realtime (qua Redis backplane).
        // Dữ liệu đã lưu thì phải trả thành công: báo realtime không được là lý do để trả 500.
        HttpResponseMessage rename = await client.PutAsJsonAsync("/api/players/me/display-name",
            new ChangeDisplayNameRequest { DisplayName = "RedisDown" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);

        HttpResponseMessage reward = await client.PostAsync("/api/players/me/daily-reward", null);
        Assert.Equal(HttpStatusCode.OK, reward.StatusCode);
        PlayerProfileResponse? profile = await reward.Content.ReadFromJsonAsync<PlayerProfileResponse>();
        Assert.Equal(100, profile!.Gold); // vàng đã được cộng thật

        HttpResponseMessage create = await client.PostAsJsonAsync("/api/clans",
            new CreateClanRequest { Name = "rd" + Guid.NewGuid().ToString("N")[..10] });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        HttpResponseMessage leave = await client.PostAsync("/api/clans/me/leave", null);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_Returns404ProblemDetails()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/this-route-does-not-exist");

        // Trước đây: 404 với nội dung RỖNG. Giờ UseStatusCodePages tự thêm ProblemDetails.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        JsonElement problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task MissingToken_Returns401ProblemDetails()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/players/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        // Header chuẩn của 401 vẫn còn: báo client "hãy gửi Bearer token".
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }
}
