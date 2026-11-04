using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Auth;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;
using StackExchange.Redis;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test dựng lại bảng xếp hạng từ PostgreSQL (POST /api/leaderboard/rebuild).
/// Tách riêng khỏi LeaderboardApiTests để có database và key Redis riêng: dựng lại sẽ nạp TẤT CẢ người chơi
/// trong database, làm sai các test đang đếm đúng số người trên bảng.
/// </summary>
public class LeaderboardRebuildApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client; // không có token, không có khoá hệ thống
    private readonly HttpClient _systemClient; // có header System-Authorization đúng

    public LeaderboardRebuildApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _systemClient = TestHelper.CreateSystemClient(factory);
    }

    [Fact]
    public async Task Rebuild_AddsPlayerWhoHadGoldBeforeLeaderboardExisted()
    {
        // A có 500 vàng từ trước (sửa thẳng trong PostgreSQL, Redis không biết).
        // B mới tạo tài khoản và nhận thưởng: 100 vàng, có trên Redis.
        TestPlayer playerA = await TestHelper.RegisterNewPlayerAsync(_factory);
        await TestHelper.SetGoldInDatabaseAsync(_factory, playerA.PlayerId, 500);
        TestPlayer playerB = await TestHelper.RegisterNewPlayerAsync(_factory);
        await playerB.Client.PostAsync("/api/players/me/daily-reward", null);

        // Trước khi dựng lại: A không có trên bảng xếp hạng.
        HttpResponseMessage beforeRebuild = await _client.GetAsync($"/api/leaderboard/players/{playerA.PlayerId}");
        Assert.Equal(HttpStatusCode.NotFound, beforeRebuild.StatusCode);

        HttpResponseMessage rebuild = await _systemClient.PostAsync("/api/leaderboard/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);

        // Sau khi dựng lại: A có mặt với đúng 500 vàng, xếp trên B.
        LeaderboardEntryResponse? rankA = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{playerA.PlayerId}");
        LeaderboardEntryResponse? rankB = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{playerB.PlayerId}");
        Assert.Equal(500, rankA!.Gold);
        Assert.Equal(PlayersController.DailyRewardGold, rankB!.Gold);
        Assert.True(rankA.Rank < rankB.Rank);
    }

    [Fact]
    public async Task Rebuild_RestoresLeaderboardAfterRedisDataIsLost()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);
        await player.Client.PostAsync("/api/players/me/daily-reward", null);

        // Giả lập Redis mất dữ liệu: DEL key bảng xếp hạng.
        IConnectionMultiplexer redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        await redis.GetDatabase().KeyDeleteAsync(_factory.LeaderboardKey);
        HttpResponseMessage afterDelete = await _client.GetAsync($"/api/leaderboard/players/{player.PlayerId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        HttpResponseMessage rebuild = await _systemClient.PostAsync("/api/leaderboard/rebuild", null);
        LeaderboardRebuildResponse? result = await rebuild.Content.ReadFromJsonAsync<LeaderboardRebuildResponse>();

        Assert.True(result!.PlayerCount >= 1);
        LeaderboardEntryResponse? entry = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{player.PlayerId}");
        Assert.Equal(PlayersController.DailyRewardGold, entry!.Gold);
    }

    // ===== Khoá hệ thống =====

    [Fact]
    public async Task Rebuild_WithoutSystemKey_ReturnsUnauthorized()
    {
        HttpResponseMessage response = await _client.PostAsync("/api/leaderboard/rebuild", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rebuild_WithWrongSystemKey_ReturnsUnauthorized()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/leaderboard/rebuild");
        request.Headers.Add(RequireSystemKeyAttribute.HeaderName, "wrong-key-0123456789-abcdefghij-0123456789");

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rebuild_WithPlayerTokenOnly_ReturnsUnauthorized()
    {
        // Token JWT hợp lệ của người chơi KHÔNG thay được khoá hệ thống: đăng nhập rồi vẫn không gọi được.
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage response = await player.Client.PostAsync("/api/leaderboard/rebuild", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
