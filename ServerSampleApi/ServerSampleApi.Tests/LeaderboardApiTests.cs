using System.Net;
using System.Net.Http.Json;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API bảng xếp hạng (LeaderboardController). Cần Redis đang chạy.</summary>
public class LeaderboardApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public LeaderboardApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task DailyReward_PutsPlayersOnLeaderboard_SortedByGold()
    {
        // Chuẩn bị: "giau" nhận thưởng 2 ngày (200 vàng), "ngheo" nhận 1 ngày (100 vàng).
        Guid poorPlayerId = await TestHelper.RegisterNewPlayerAsync(_client);
        Guid richPlayerId = await TestHelper.RegisterNewPlayerAsync(_client);
        await _client.PutAsJsonAsync($"/api/players/{richPlayerId}/display-name",
            new ChangeDisplayNameRequest { DisplayName = "Dai Gia" });

        await _client.PostAsync($"/api/players/{poorPlayerId}/daily-reward", null);
        await _client.PostAsync($"/api/players/{richPlayerId}/daily-reward", null);
        await TestHelper.SetLastDailyRewardToYesterdayAsync(_factory, richPlayerId);
        await _client.PostAsync($"/api/players/{richPlayerId}/daily-reward", null);

        // Hành động
        LeaderboardResponse? leaderboard = await _client.GetFromJsonAsync<LeaderboardResponse>("/api/leaderboard?count=10");

        // Kiểm tra: người nhiều vàng hơn đứng đầu, hạng bắt đầu từ 1, có tên lấy từ PostgreSQL.
        Assert.Equal(2, leaderboard!.Entries.Count);

        LeaderboardEntryResponse first = leaderboard.Entries[0];
        Assert.Equal(1, first.Rank);
        Assert.Equal(richPlayerId, first.PlayerId);
        Assert.Equal("Dai Gia", first.DisplayName);
        Assert.Equal(2 * PlayersController.DailyRewardGold, first.Gold);

        LeaderboardEntryResponse second = leaderboard.Entries[1];
        Assert.Equal(2, second.Rank);
        Assert.Equal(poorPlayerId, second.PlayerId);
        Assert.Equal(PlayersController.DailyRewardGold, second.Gold);

        // Hỏi riêng hạng của người nghèo cũng phải ra hạng 2.
        LeaderboardEntryResponse? poorRank = await _client.GetFromJsonAsync<LeaderboardEntryResponse>(
            $"/api/leaderboard/players/{poorPlayerId}");
        Assert.Equal(2, poorRank!.Rank);
    }

    [Fact]
    public async Task PlayerNotOnLeaderboard_ReturnsNotFound()
    {
        // Mới đăng ký, chưa nhận vàng lần nào nên chưa có trên bảng.
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage response = await _client.GetAsync($"/api/leaderboard/players/{playerId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetTop_CountOutOfRange_ReturnsBadRequest(int count)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/leaderboard?count={count}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
