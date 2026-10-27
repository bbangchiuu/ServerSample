using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerSample.Api.Accounts;

namespace ServerSample.Api.Tests;

/// <summary>
/// Integration test: WebApplicationFactory dựng nguyên server (Program.cs) trong bộ nhớ, không mở cổng mạng thật.
/// Các request đi qua đủ middleware, validation, endpoint giống hệt khi chạy thật.
///
/// Cả class dùng chung một server (IClassFixture), nên mỗi test tự tạo username riêng để không đụng nhau.
/// </summary>
public class AccountApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Register_ReturnsCreated_WithLocationOfPlayer()
    {
        string username = TestData.NewUsername();

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(username, "secret123"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AccountResponse? body = await response.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(username, body!.Username);
        Assert.Equal($"/api/players/{body.PlayerId}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Register_SameUsernameDifferentCase_ReturnsConflict()
    {
        string username = TestData.NewUsername();
        await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(username, "secret123"));

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(username.ToUpperInvariant(), "secret123"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Register_InvalidInput_ReturnsValidationErrorsForEachField()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest("a!", "123"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"Username\"", body);
        Assert.Contains("\"Password\"", body);
    }

    [Fact]
    public async Task Login_WithCorrectPassword_ReturnsSamePlayerId()
    {
        string username = TestData.NewUsername();
        HttpResponseMessage registered = await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(username, "secret123"));
        AccountResponse? account = await registered.Content.ReadFromJsonAsync<AccountResponse>();

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/login", new LoginRequest(username, "secret123"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AccountResponse? body = await response.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(account!.PlayerId, body!.PlayerId);
    }

    [Theory]
    [InlineData("wrong-password")]
    [InlineData(null)] // null = tên tài khoản không tồn tại
    public async Task Login_WrongPasswordOrUnknownUser_ReturnsSameUnauthorizedError(string? password)
    {
        string username = TestData.NewUsername();
        if (password is not null)
            await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(username, "secret123"));

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/login", new LoginRequest(username, password ?? "secret123"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid username or password", await response.Content.ReadAsStringAsync());
    }
}
