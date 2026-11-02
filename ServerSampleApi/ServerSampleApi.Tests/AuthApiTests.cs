using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.IdentityModel.Tokens;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test bảo mật: các API của người chơi phải từ chối mọi request không có token hợp lệ,
/// và người này không bao giờ đụng được dữ liệu của người kia.
/// </summary>
public class AuthApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client; // không có token

    public AuthApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("GET", "/api/players/me")]
    [InlineData("PUT", "/api/players/me/display-name")]
    [InlineData("POST", "/api/players/me/daily-reward")]
    [InlineData("GET", "/api/players/me/inventory")]
    [InlineData("POST", "/api/players/me/inventory/potion/use")]
    public async Task PlayerApis_WithoutToken_ReturnUnauthorized(string method, string url)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "PUT")
        {
            request.Content = JsonContent.Create(new ChangeDisplayNameRequest { DisplayName = "Hacker" });
        }

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithAnotherKey_ReturnsUnauthorized()
    {
        // Kẻ xấu tự tạo token cho một người chơi có thật, nhưng không có khoá thật nên ký bằng khoá khác.
        TestPlayer victim = await TestHelper.RegisterNewPlayerAsync(_factory);
        string fakeToken = TestHelper.CreateToken(victim.PlayerId, DateTime.UtcNow.AddHours(1),
            signingKey: "attacker-guessed-key-0123456789-abcdefghij");

        HttpResponseMessage response = await SendWithTokenAsync("/api/players/me", fakeToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TamperedToken_ChangingSubToAnotherPlayer_ReturnsUnauthorized()
    {
        // Kẻ xấu có token THẬT của mình, sửa "sub" trong payload thành playerId của nạn nhân,
        // giữ nguyên chữ ký cũ. Payload đổi nên chữ ký không còn khớp.
        TestPlayer attacker = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer victim = await TestHelper.RegisterNewPlayerAsync(_factory);
        string attackerToken = attacker.Client.DefaultRequestHeaders.Authorization!.Parameter!;

        string[] parts = attackerToken.Split('.'); // header.payload.signature
        string payloadJson = Base64UrlEncoder.Decode(parts[1]); // payload chỉ là Base64, ai cũng đọc được
        Assert.Contains(attacker.PlayerId.ToString(), payloadJson);
        parts[1] = Base64UrlEncoder.Encode(payloadJson.Replace(attacker.PlayerId.ToString(), victim.PlayerId.ToString()));
        string tamperedToken = string.Join('.', parts);

        HttpResponseMessage response = await SendWithTokenAsync("/api/players/me", tamperedToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsUnauthorized()
    {
        // Token ký đúng khoá, nhưng đã hết hạn 2 phút (lâu hơn 30 giây ClockSkew cho phép).
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);
        string expiredToken = TestHelper.CreateToken(player.PlayerId, DateTime.UtcNow.AddMinutes(-2));

        HttpResponseMessage response = await SendWithTokenAsync("/api/players/me", expiredToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // Server ghi lý do trong header WWW-Authenticate, ví dụ: Bearer error="invalid_token", error_description="The token expired at ..."
        Assert.Contains("expired", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task ValidTokenForPlayerThatDoesNotExist_ReturnsNotFound()
    {
        // Token hợp lệ (đúng khoá) nhưng người chơi không có trong database, ví dụ tài khoản đã bị xoá.
        string token = TestHelper.CreateToken(Guid.NewGuid(), DateTime.UtcNow.AddHours(1));

        HttpResponseMessage response = await SendWithTokenAsync("/api/players/me", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EachPlayer_OnlySeesAndChangesTheirOwnData()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Alice nhận thưởng: chỉ Alice có vàng, Bob vẫn 0.
        await alice.Client.PostAsync("/api/players/me/daily-reward", null);

        PlayerProfileResponse? aliceProfile = await alice.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");
        PlayerProfileResponse? bobProfile = await bob.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");
        Assert.Equal(alice.PlayerId, aliceProfile!.Id);
        Assert.Equal(100, aliceProfile.Gold);
        Assert.Equal(bob.PlayerId, bobProfile!.Id);
        Assert.Equal(0, bobProfile.Gold);
    }

    private async Task<HttpResponseMessage> SendWithTokenAsync(string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request);
    }
}
