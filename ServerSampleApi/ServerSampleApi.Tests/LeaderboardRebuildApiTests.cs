using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly HttpClient _client;

    public LeaderboardRebuildApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Rebuild_AddsPlayerWhoHadGoldBeforeLeaderboardExisted()
    {
        // A có 500 vàng từ trước (sửa thẳng trong PostgreSQL, Redis không biết).
        // B mới tạo tài khoản và nhận thưởng: 100 vàng, có trên Redis.
        Guid playerA = await TestHelper.RegisterNewPlayerAsync(_client);
        await TestHelper.SetGoldInDatabaseAsync(_factory, playerA, 500);
        Guid playerB = await TestHelper.RegisterNewPlayerAsync(_client);
        await _client.PostAsync($"/api/players/{playerB}/daily-reward", null);

        // Trước khi dựng lại: A không có trên bảng xếp hạng (đúng vấn đề bạn hỏi).
        HttpResponseMessage beforeRebuild = await _client.GetAsync($"/api/leaderboard/players/{playerA}");
        Assert.Equal(HttpStatusCode.NotFound, beforeRebuild.StatusCode);

        HttpResponseMessage rebuild = await _client.PostAsync("/api/leaderboard/rebuild", null);
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);

        // Sau khi dựng lại: A có mặt với đúng 500 vàng, xếp trên B.
        LeaderboardEntryResponse? rankA = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{playerA}");
        LeaderboardEntryResponse? rankB = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{playerB}");
        Assert.Equal(500, rankA!.Gold);
        Assert.Equal(PlayersController.DailyRewardGold, rankB!.Gold);
        Assert.True(rankA.Rank < rankB.Rank);
    }

    [Fact]
    public async Task Rebuild_RestoresLeaderboardAfterRedisDataIsLost()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);
        await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);

        // Giả lập Redis mất dữ liệu: DEL key bảng xếp hạng.
        IConnectionMultiplexer redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>();
        await redis.GetDatabase().KeyDeleteAsync(_factory.LeaderboardKey);
        HttpResponseMessage afterDelete = await _client.GetAsync($"/api/leaderboard/players/{playerId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        HttpResponseMessage rebuild = await _client.PostAsync("/api/leaderboard/rebuild", null);
        LeaderboardRebuildResponse? result = await rebuild.Content.ReadFromJsonAsync<LeaderboardRebuildResponse>();

        Assert.True(result!.PlayerCount >= 1);
        LeaderboardEntryResponse? entry = await _client.GetFromJsonAsync<LeaderboardEntryResponse>($"/api/leaderboard/players/{playerId}");
        Assert.Equal(PlayersController.DailyRewardGold, entry!.Gold);
    }
}
