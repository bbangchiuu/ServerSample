using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API người chơi (PlayersController): profile, đổi tên, thưởng hằng ngày.</summary>
public class PlayersApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PlayersApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task NewPlayer_HasDefaultProfile()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");

        Assert.Equal(1, profile!.Level);
        Assert.Equal(0, profile.Gold);
        Assert.Null(profile.LastDailyRewardDate);
    }

    [Fact]
    public async Task UnknownPlayer_ReturnsNotFound()
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/players/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChangeDisplayName_TrimsSpaces_AndIsSaved()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage response = await _client.PutAsJsonAsync($"/api/players/{playerId}/display-name",
            new ChangeDisplayNameRequest { DisplayName = "  Hiep Si  " });
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hiep Si", profile!.DisplayName);
    }

    [Fact]
    public async Task ChangeDisplayName_TooShortAfterTrimming_ReturnsBadRequest()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        // 8 ký tự nên qua được [StringLength], nhưng bỏ khoảng trắng chỉ còn "ab".
        HttpResponseMessage response = await _client.PutAsJsonAsync($"/api/players/{playerId}/display-name",
            new ChangeDisplayNameRequest { DisplayName = "   ab   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DailyReward_CanBeClaimedOnlyOncePerDay()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage first = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);
        HttpResponseMessage second = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_20RequestsAtTheSameTime_OnlyOneSucceeds()
    {
        // Giả lập người chơi gian lận: bắn 20 request "nhận thưởng" cùng một lúc.
        // Thử bằng tay rất khó làm được; test thì làm được, và lần nào chạy cũng kiểm tra lại.
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        // Tạo 20 Task cùng chạy, rồi chờ tất cả xong.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync($"/api/players/{playerId}/daily-reward", null))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        // Nhờ lock trong ClaimDailyReward: đúng 1 request thành công, và chỉ được cộng vàng 1 lần.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }
}
