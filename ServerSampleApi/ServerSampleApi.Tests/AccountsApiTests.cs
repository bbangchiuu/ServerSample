using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test API tài khoản (AccountsController).
///
/// Đây là INTEGRATION TEST: WebApplicationFactory dựng NGUYÊN server (đúng Program.cs của API)
/// ngay trong bộ nhớ, không mở cổng mạng thật. Request đi qua đủ middleware, validation, controller
/// giống hệt khi Unity gọi tới. Không cần bật server trước khi chạy test.
///
/// IClassFixture: cả class dùng chung MỘT server (tạo một lần, nhanh hơn tạo mới cho từng test).
/// </summary>
public class AccountsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public AccountsApiTests(WebApplicationFactory<Program> factory)
    {
        // HttpClient đã trỏ sẵn tới server ảo.
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Register_ReturnsCreated_AndLocationPointsToTheNewProfile()
    {
        // Chuẩn bị
        string username = TestHelper.NewUsername();

        // Hành động
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = username, Password = TestHelper.Password });

        // Kiểm tra
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(username, account!.Username);

        // Header Location phải dẫn tới đúng profile vừa tạo.
        Assert.NotNull(response.Headers.Location);
        HttpResponseMessage profileResponse = await _client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, profileResponse.StatusCode);
        PlayerProfileResponse? profile = await profileResponse.Content.ReadFromJsonAsync<PlayerProfileResponse>();
        Assert.Equal(account.PlayerId, profile!.Id);
    }

    [Fact]
    public async Task Register_SameUsernameDifferentCase_ReturnsConflict()
    {
        string username = TestHelper.NewUsername();
        await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest { Username = username, Password = TestHelper.Password });

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = username.ToUpperInvariant(), Password = TestHelper.Password });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_InvalidInput_ReturnsBadRequestWithErrorForEachField()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = "a!", Password = "123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"Username\"", body);
        Assert.Contains("\"Password\"", body);
    }

    [Fact]
    public async Task Login_CorrectPassword_ReturnsSamePlayerIdAsRegister()
    {
        string username = TestHelper.NewUsername();
        HttpResponseMessage registered = await _client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = username, Password = TestHelper.Password });
        AccountResponse? account = await registered.Content.ReadFromJsonAsync<AccountResponse>();

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/login",
            new LoginRequest { Username = username, Password = TestHelper.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AccountResponse? loggedIn = await response.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(account!.PlayerId, loggedIn!.PlayerId);
    }

    // [Theory] + [InlineData]: CÙNG một test, chạy lại với nhiều bộ dữ liệu khác nhau.
    [Theory]
    [InlineData(true)]  // tài khoản có thật, nhưng sai mật khẩu
    [InlineData(false)] // tài khoản không tồn tại
    public async Task Login_WrongPasswordOrUnknownUser_ReturnsTheSameUnauthorizedError(bool accountExists)
    {
        string username = TestHelper.NewUsername();
        if (accountExists)
        {
            await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest { Username = username, Password = TestHelper.Password });
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/login",
            new LoginRequest { Username = username, Password = "wrong-password" });

        // Hai trường hợp phải trả về y hệt nhau, để không lộ ra tài khoản nào có thật.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid username or password", await response.Content.ReadAsStringAsync());
    }
}
