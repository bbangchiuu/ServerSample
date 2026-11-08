using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test danh sách người chơi online (PresenceService + GameHub): ai vào thì mọi người được báo PlayerOnline,
/// ai đóng kết nối CUỐI CÙNG thì được báo PlayerOffline, và GetOnlinePlayers trả đúng danh sách.
/// </summary>
public class PresenceTests : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ApiFactory _factory;

    public PresenceTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PlayerConnects_OthersAreNotified_AndPlayerIsInOnlineList()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        await bob.Client.PutAsJsonAsync("/api/players/me/display-name", new ChangeDisplayNameRequest { DisplayName = "Bob" });

        await using HubConnection aliceConnection = await TestHelper.ConnectToHubAsync(_factory, alice);
        Task<OnlinePlayer> bobCameOnline = TestHelper.WaitForEventAsync<OnlinePlayer>(
            aliceConnection, nameof(IGameClient.PlayerOnline), p => p.PlayerId == bob.PlayerId);

        await using HubConnection bobConnection = await TestHelper.ConnectToHubAsync(_factory, bob);

        OnlinePlayer online = await bobCameOnline.WaitAsync(Timeout);
        Assert.Equal("Bob", online.DisplayName);

        // Hàm hub CÓ giá trị trả về: InvokeAsync<T> chờ gói Completion và đọc "result".
        OnlinePlayersResponse list = await aliceConnection.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        Assert.Contains(list.Players, p => p.PlayerId == alice.PlayerId);
        Assert.Contains(list.Players, p => p.PlayerId == bob.PlayerId && p.DisplayName == "Bob");
    }

    [Fact]
    public async Task PlayerWithTwoDevices_GoesOfflineOnlyWhenLastConnectionCloses()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        await using HubConnection aliceConnection = await TestHelper.ConnectToHubAsync(_factory, alice);
        Task<OnlinePlayer> bobWentOffline = TestHelper.WaitForEventAsync<OnlinePlayer>(
            aliceConnection, nameof(IGameClient.PlayerOffline), p => p.PlayerId == bob.PlayerId);

        HubConnection bobPhone = await TestHelper.ConnectToHubAsync(_factory, bob);
        HubConnection bobPc = await TestHelper.ConnectToHubAsync(_factory, bob);

        // Tắt điện thoại: máy tính vẫn mở nên Bob vẫn online, không ai được báo offline.
        await bobPhone.DisposeAsync();
        await Task.Delay(300);
        Assert.False(bobWentOffline.IsCompleted);
        OnlinePlayersResponse whilePcOpen = await aliceConnection.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        Assert.Contains(whilePcOpen.Players, p => p.PlayerId == bob.PlayerId);

        // Tắt nốt máy tính: kết nối cuối cùng đóng, Bob offline.
        await bobPc.DisposeAsync();
        await bobWentOffline.WaitAsync(Timeout);
        OnlinePlayersResponse afterBothClosed = await aliceConnection.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        Assert.DoesNotContain(afterBothClosed.Players, p => p.PlayerId == bob.PlayerId);
    }
}
