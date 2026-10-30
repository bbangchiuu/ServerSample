using System.Net;
using System.Net.Http.Json;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API túi đồ (InventoryController).</summary>
public class InventoryApiTests : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client;

    public InventoryApiTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task NewPlayer_HasEmptyInventory()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        InventoryResponse? inventory = await _client.GetFromJsonAsync<InventoryResponse>($"/api/players/{playerId}/inventory");

        Assert.Empty(inventory!.Items);
    }

    [Fact]
    public async Task UseItem_NotOwned_ReturnsConflict()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage response = await _client.PostAsync($"/api/players/{playerId}/inventory/potion/use", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task DailyReward_GivesOnePotion_AndUsingItRemovesIt()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);
        InventoryResponse? afterReward = await _client.GetFromJsonAsync<InventoryResponse>($"/api/players/{playerId}/inventory");

        InventoryItemResponse potion = Assert.Single(afterReward!.Items);
        Assert.Equal(PlayersController.DailyRewardItemId, potion.ItemId);
        Assert.Equal(1, potion.Quantity);

        // Mã vật phẩm không phân biệt hoa thường: "POTION" cũng là "potion".
        HttpResponseMessage useResponse = await _client.PostAsync($"/api/players/{playerId}/inventory/POTION/use", null);
        InventoryResponse? afterUse = await useResponse.Content.ReadFromJsonAsync<InventoryResponse>();

        Assert.Equal(HttpStatusCode.OK, useResponse.StatusCode);
        Assert.Empty(afterUse!.Items); // dùng hết thì vật phẩm bị xoá khỏi túi
    }

    [Fact]
    public async Task UseItem_10RequestsAtTheSameTimeWithOnlyOnePotion_OnlyOneSucceeds()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);
        await _client.PostAsync($"/api/players/{playerId}/daily-reward", null); // có đúng 1 potion

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _client.PostAsync($"/api/players/{playerId}/inventory/potion/use", null)));

        // 1 bình máu chỉ được dùng đúng 1 lần, dù bấm nhanh đến đâu.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(9, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
    }
}
