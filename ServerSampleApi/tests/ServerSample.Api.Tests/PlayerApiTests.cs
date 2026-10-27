using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerSample.Api.Accounts;
using ServerSample.Api.Players;

namespace ServerSample.Api.Tests;

public class PlayerApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task NewPlayer_HasDefaultProfileAndEmptyInventory()
    {
        Guid playerId = await RegisterAsync();

        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        InventoryResponse? inventory = await _client.GetFromJsonAsync<InventoryResponse>($"/api/players/{playerId}/inventory");

        Assert.Equal(1, profile!.Level);
        Assert.Equal(0, profile.Gold);
        Assert.Null(profile.LastDailyRewardDate);
        Assert.Empty(inventory!.Items);
    }

    [Fact]
    public async Task UnknownPlayer_ReturnsNotFoundAsProblemDetails()
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/players/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ChangeDisplayName_IsSaved()
    {
        Guid playerId = await RegisterAsync();

        HttpResponseMessage response = await _client.PutAsJsonAsync($"/api/players/{playerId}/display-name", new ChangeDisplayNameRequest("Hiep Si"));
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hiep Si", profile!.DisplayName);
    }

    [Fact]
    public async Task DailyReward_CanBeClaimedOnlyOncePerDay()
    {
        Guid playerId = await RegisterAsync();

        HttpResponseMessage first = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);
        HttpResponseMessage second = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayerEndpoints.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_ManyParallelRequests_OnlyOneSucceeds()
    {
        // Giả lập client gian lận: bắn 20 request "nhận thưởng" cùng một lúc.
        // Nhờ lock trong ClaimDailyReward, chỉ đúng 1 request được nhận thưởng.
        Guid playerId = await RegisterAsync();

        HttpResponseMessage[] responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => _client.PostAsync($"/api/players/{playerId}/daily-reward", null)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayerEndpoints.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task UseItem_RequiresOwningTheItem()
    {
        Guid playerId = await RegisterAsync();
        string useUrl = $"/api/players/{playerId}/inventory/{PlayerEndpoints.DailyRewardItemId}/use";

        HttpResponseMessage beforeReward = await _client.PostAsync(useUrl, null);
        await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);
        HttpResponseMessage afterReward = await _client.PostAsync(useUrl, null);

        Assert.Equal(HttpStatusCode.Conflict, beforeReward.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterReward.StatusCode);
        InventoryResponse? inventory = await afterReward.Content.ReadFromJsonAsync<InventoryResponse>();
        Assert.Empty(inventory!.Items);
    }

    private async Task<Guid> RegisterAsync()
    {
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/accounts/register", new RegisterRequest(TestData.NewUsername(), "secret123"));
        response.EnsureSuccessStatusCode();
        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.PlayerId;
    }
}
