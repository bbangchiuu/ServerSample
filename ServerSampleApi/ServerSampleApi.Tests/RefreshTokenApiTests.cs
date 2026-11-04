using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test refresh token: làm mới (/api/accounts/refresh) và đăng xuất (/api/accounts/logout).
/// </summary>
public class RefreshTokenApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client; // không có token

    public RefreshTokenApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_ReturnsRefreshTokenValidFor30Days()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        Assert.False(string.IsNullOrEmpty(account.RefreshToken));
        Assert.InRange(account.RefreshTokenExpiresAt, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
    }

    [Fact]
    public async Task Refresh_ReturnsNewTokenPairThatWorks()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        HttpResponseMessage response = await RefreshAsync(account.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AccountResponse? refreshed = await response.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(account.PlayerId, refreshed!.PlayerId);
        Assert.NotEqual(account.AccessToken, refreshed.AccessToken);
        Assert.NotEqual(account.RefreshToken, refreshed.RefreshToken); // xoay vòng: refresh token mới

        // Access token mới dùng được cho các API của người chơi.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/players/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);
        HttpResponseMessage profile = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    [Fact]
    public async Task Refresh_SameTokenTwice_SecondTimeReturnsUnauthorized()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        HttpResponseMessage first = await RefreshAsync(account.RefreshToken);
        HttpResponseMessage second = await RefreshAsync(account.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Refresh_ReusingOldToken_RevokesNewestTokenToo()
    {
        // Kẻ xấu lấy trộm được refresh token. Người chơi thật làm mới trước, nhận token mới.
        // Sau đó kẻ xấu dùng token trộm (đã bị dùng rồi): server nhận ra token bị dùng lại,
        // thu hồi luôn token mới của người chơi. Cả hai đều phải đăng nhập lại, và chỉ người chơi thật biết mật khẩu.
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());
        string stolenToken = account.RefreshToken;

        HttpResponseMessage realPlayerRefresh = await RefreshAsync(account.RefreshToken);
        AccountResponse? realPlayer = await realPlayerRefresh.Content.ReadFromJsonAsync<AccountResponse>();

        HttpResponseMessage attackerRefresh = await RefreshAsync(stolenToken);
        HttpResponseMessage realPlayerNextRefresh = await RefreshAsync(realPlayer!.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, attackerRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, realPlayerNextRefresh.StatusCode);
    }

    [Fact]
    public async Task Logout_ThenRefresh_ReturnsUnauthorized()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        HttpResponseMessage logout = await LogoutAsync(account.RefreshToken);
        HttpResponseMessage refresh = await RefreshAsync(account.RefreshToken);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Logout_OnOneDevice_DoesNotLogOutOtherDevice()
    {
        // Đăng nhập trên 2 máy: mỗi máy một refresh token riêng.
        string username = TestHelper.NewUsername();
        AccountResponse phone = await RegisterAsync(username);
        AccountResponse pc = await LoginAsync(username);

        await LogoutAsync(phone.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(phone.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(pc.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Logout_WithUnknownToken_StillReturnsNoContent()
    {
        HttpResponseMessage response = await LogoutAsync("token-that-does-not-exist");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_UnknownToken_ReturnsUnauthorized()
    {
        HttpResponseMessage response = await RefreshAsync("token-that-does-not-exist");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_ExpiredToken_ReturnsUnauthorized()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        await ExpireInDatabaseAsync(account.RefreshToken);

        HttpResponseMessage response = await RefreshAsync(account.RefreshToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteExpired_RemovesOnlyExpiredTokens()
    {
        AccountResponse active = await RegisterAsync(TestHelper.NewUsername());
        AccountResponse loggedOut = await RegisterAsync(TestHelper.NewUsername());
        AccountResponse expired = await RegisterAsync(TestHelper.NewUsername());
        await LogoutAsync(loggedOut.RefreshToken);
        await ExpireInDatabaseAsync(expired.RefreshToken);

        using IServiceScope scope = _factory.Services.CreateScope();
        RefreshTokenService refreshTokens = scope.ServiceProvider.GetRequiredService<RefreshTokenService>();
        int deleted = await refreshTokens.DeleteExpiredAsync();

        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        Assert.True(deleted >= 1);
        Assert.False(await db.RefreshTokens.AnyAsync(t => t.AccountId == expired.PlayerId));
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.AccountId == active.PlayerId));
        // Đã thu hồi nhưng còn hạn thì GIỮ LẠI: vẫn cần để phát hiện token bị dùng lại.
        Assert.True(await db.RefreshTokens.AnyAsync(t => t.AccountId == loggedOut.PlayerId));
    }

    /// <summary>Giả lập đã 30 ngày trôi qua: sửa thẳng hạn dùng của token trong database thành hôm qua.</summary>
    private async Task ExpireInDatabaseAsync(string refreshToken)
    {
        string tokenHash = RefreshTokenService.Hash(refreshToken);
        using IServiceScope scope = _factory.Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        await db.RefreshTokens
            .Where(t => t.TokenHash == tokenHash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.ExpiresAt, DateTime.UtcNow.AddDays(-1)));
    }

    [Fact]
    public async Task Database_StoresOnlyHashOfRefreshToken()
    {
        AccountResponse account = await RegisterAsync(TestHelper.NewUsername());

        using IServiceScope scope = _factory.Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        RefreshTokenModel stored = await db.RefreshTokens.SingleAsync(t => t.AccountId == account.PlayerId);

        // Lộ database thì kẻ xấu chỉ thấy bản băm, không có token gốc để gửi lên server.
        Assert.NotEqual(account.RefreshToken, stored.TokenHash);
        Assert.Equal(RefreshTokenService.Hash(account.RefreshToken), stored.TokenHash);
    }

    private async Task<AccountResponse> RegisterAsync(string username)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = username, Password = TestHelper.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private async Task<AccountResponse> LoginAsync(string username)
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/login",
            new LoginRequest { Username = username, Password = TestHelper.Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken) =>
        _client.PostAsJsonAsync("/api/accounts/refresh", new RefreshTokenRequest { RefreshToken = refreshToken });

    private Task<HttpResponseMessage> LogoutAsync(string refreshToken) =>
        _client.PostAsJsonAsync("/api/accounts/logout", new RefreshTokenRequest { RefreshToken = refreshToken });
}
