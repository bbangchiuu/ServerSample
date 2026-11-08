using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;
using ServerSampleApi.Services;
using StackExchange.Redis;

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
    public async Task DeadServer_ItsPlayersAreRemoved_AndOthersAreNotified()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        await using HubConnection aliceConnection = await TestHelper.ConnectToHubAsync(_factory, alice);
        Task<OnlinePlayer> bobWentOffline = TestHelper.WaitForEventAsync<OnlinePlayer>(
            aliceConnection, nameof(IGameClient.PlayerOffline), p => p.PlayerId == bob.PlayerId);

        // Giả lập: Bob đang nối vào một server khác, và server đó bị giết đột ngột 1 phút trước
        // (heartbeat cuối cùng cách đây 60 giây, không ai kịp trừ bộ đếm của Bob).
        await SimulateDeadServerWithPlayerAsync(bob.PlayerId);
        Assert.Contains(await GetOnlinePlayerIdsAsync(aliceConnection), id => id == bob.PlayerId); // Bob đang "kẹt" online

        // Một nhịp heartbeat của server còn sống: phát hiện server chết, dọn phần của nó.
        await GetHeartbeatService().BeatAsync();

        await bobWentOffline.WaitAsync(Timeout);
        Assert.DoesNotContain(await GetOnlinePlayerIdsAsync(aliceConnection), id => id == bob.PlayerId);
        Assert.Contains(await GetOnlinePlayerIdsAsync(aliceConnection), id => id == alice.PlayerId); // Alice không bị ảnh hưởng
    }

    [Fact]
    public async Task DeadServer_PlayerStillConnectedToLiveServer_StaysOnline()
    {
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Bob có một kết nối "kẹt" trên server đã chết, và một kết nối thật trên server còn sống.
        await SimulateDeadServerWithPlayerAsync(bob.PlayerId);
        await using HubConnection bobConnection = await TestHelper.ConnectToHubAsync(_factory, bob);

        await GetHeartbeatService().BeatAsync();

        // Chỉ phần trên server chết bị trừ: Bob vẫn còn 1 kết nối thật nên vẫn online.
        Assert.Contains(await GetOnlinePlayerIdsAsync(bobConnection), id => id == bob.PlayerId);
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

    /// <summary>
    /// Ghi thẳng vào Redis đúng những gì một server khác để lại nếu nó bị giết khi người chơi đang kết nối:
    /// heartbeat cũ 60 giây, "phần" của nó có 1 kết nối của người chơi, tổng số kết nối của người chơi +1.
    /// </summary>
    private async Task SimulateDeadServerWithPlayerAsync(Guid playerId)
    {
        string deadServerId = "dead-" + Guid.NewGuid().ToString("N");
        IDatabase redis = _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        long oneMinuteAgo = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();

        await redis.SortedSetAddAsync($"{_factory.PresenceKeyPrefix}:servers", deadServerId, oneMinuteAgo);
        await redis.HashSetAsync($"{_factory.PresenceKeyPrefix}:server:{deadServerId}", playerId.ToString(), 1);
        await redis.HashIncrementAsync($"{_factory.PresenceKeyPrefix}:players", playerId.ToString(), 1);
    }

    private PresenceHeartbeatService GetHeartbeatService() =>
        _factory.Services.GetServices<IHostedService>().OfType<PresenceHeartbeatService>().Single();

    private static async Task<List<Guid>> GetOnlinePlayerIdsAsync(HubConnection connection)
    {
        OnlinePlayersResponse list = await connection.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        return list.Players.Select(p => p.PlayerId).ToList();
    }
}
