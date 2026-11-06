using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Server ảo giống ApiFactory, nhưng giới hạn rất thấp (3 lần) để thử được lỗi 429 mà không phải gửi hàng trăm request.
/// UseSetting gọi SAU base.ConfigureWebHost nên ghi đè giá trị 10000 của ApiFactory.
/// </summary>
public class StrictRateLimitApiFactory : ApiFactory
{
    public const int Limit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:LoginPermitLimit", Limit.ToString());
        builder.UseSetting("RateLimiting:RegisterPermitLimit", Limit.ToString());
    }
}

/// <summary>
/// Test giới hạn số lần gọi /login và /register.
/// Bộ đếm chạy theo thời gian thật (cửa sổ 60 giây / 1 giờ) và các test trong lớp này dùng chung một server,
/// nên mỗi loại giới hạn chỉ thử trong MỘT test: test thứ hai sẽ thấy bộ đếm đã bị test đầu dùng hết.
/// </summary>
public class RateLimitApiTests : IClassFixture<StrictRateLimitApiFactory>
{
    private readonly HttpClient _client;

    public RateLimitApiTests(StrictRateLimitApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Login_TooManyAttempts_ReturnsTooManyRequests()
    {
        // Kẻ xấu dò mật khẩu: 3 lần sai đầu tiên server vẫn kiểm tra và trả 401.
        for (int i = 0; i < StrictRateLimitApiFactory.Limit; i++)
        {
            HttpResponseMessage wrong = await LoginAsync("victim", "guess" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Lần thứ 4: bị chặn trước khi tới controller, kể cả khi lần này đoán ĐÚNG mật khẩu.
        HttpResponseMessage blocked = await LoginAsync("victim", TestHelper.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter); // header Retry-After: còn bao nhiêu giây nữa thì thử lại được
        ProblemDetails? problem = await blocked.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Too many requests", problem!.Title);
    }

    [Fact]
    public async Task Register_TooManyAccounts_ReturnsTooManyRequests()
    {
        for (int i = 0; i < StrictRateLimitApiFactory.Limit; i++)
        {
            HttpResponseMessage created = await RegisterAsync(TestHelper.NewUsername());
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        HttpResponseMessage blocked = await RegisterAsync(TestHelper.NewUsername());

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task OtherApis_AreNotLimited()
    {
        // API không có [EnableRateLimiting] thì gọi bao nhiêu lần cũng được.
        for (int i = 0; i < StrictRateLimitApiFactory.Limit * 3; i++)
        {
            HttpResponseMessage response = await _client.GetAsync("/api/leaderboard?count=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private Task<HttpResponseMessage> LoginAsync(string username, string password) =>
        _client.PostAsJsonAsync("/api/accounts/login", new LoginRequest { Username = username, Password = password });

    private Task<HttpResponseMessage> RegisterAsync(string username) =>
        _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest { Username = username, Password = TestHelper.Password });
}
