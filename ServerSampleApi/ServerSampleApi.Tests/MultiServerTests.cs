using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;

namespace ServerSampleApi.Tests;

/// <summary>
/// Chạy HAI server cùng lúc (giống hai máy chủ đứng sau một load balancer): dùng chung PostgreSQL và Redis,
/// nhưng mỗi server có bộ nhớ riêng, chỉ giữ các kết nối SignalR của chính nó.
/// Nhờ Redis backplane, gửi từ server này vẫn tới được người chơi đang nối vào server kia.
/// </summary>
public class MultiServerTests : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ApiFactory _server1;

    public MultiServerTests(ApiFactory factory)
    {
        _server1 = factory;
    }

    /// <summary>
    /// Server thứ hai: WithWebHostBuilder tạo một server MỚI, chạy lại đúng cấu hình của ApiFactory (cùng database test,
    /// cùng key Redis, cùng kênh backplane), nhưng là một process ảo khác với bộ nhớ SignalR riêng.
    /// </summary>
    private WebApplicationFactory<Program> StartServer2() => _server1.WithWebHostBuilder(_ => { });

    [Fact]
    public async Task ChatMessage_FromServer1_ReachesPlayerOnServer2()
    {
        await using WebApplicationFactory<Program> server2 = StartServer2();
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_server1);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_server1);

        await using HubConnection aliceOnServer1 = await TestHelper.ConnectToHubAsync(_server1, alice);
        await using HubConnection bobOnServer2 = await TestHelper.ConnectToHubAsync(server2, bob);
        Task<ChatMessage> bobReceived = TestHelper.WaitForEventAsync<ChatMessage>(
            bobOnServer2, nameof(IGameClient.ReceiveChatMessage), m => m.PlayerId == alice.PlayerId);

        // Server 1 gọi Clients.All: chính nó chỉ biết kết nối của Alice. Backplane chuyển tin sang server 2 cho Bob.
        await aliceOnServer1.InvokeAsync(nameof(GameHub.SendChatMessage), "hello from server 1");

        ChatMessage message = await bobReceived.WaitAsync(Timeout);
        Assert.Equal("hello from server 1", message.Text);
    }

    [Fact]
    public async Task HttpRequestOnServer1_PushesProfileToPlayerConnectedToServer2()
    {
        await using WebApplicationFactory<Program> server2 = StartServer2();
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_server1);

        // Bob mở game, kết nối realtime vào server 2; request nhận thưởng lại do load balancer chuyển tới server 1.
        await using HubConnection bobOnServer2 = await TestHelper.ConnectToHubAsync(server2, bob);
        Task<PlayerProfileResponse> bobReceived = TestHelper.WaitForEventAsync<PlayerProfileResponse>(
            bobOnServer2, nameof(IGameClient.ProfileChanged));

        HttpResponseMessage response = await bob.Client.PostAsync("/api/players/me/daily-reward", null);
        response.EnsureSuccessStatusCode();

        // Clients.User(bob) trên server 1 → backplane → server 2 biết kết nối nào là của Bob.
        PlayerProfileResponse profile = await bobReceived.WaitAsync(Timeout);
        Assert.Equal(PlayersController.DailyRewardGold, profile.Gold);
    }

    [Fact]
    public async Task PlayerConnectingToServer2_IsAnnouncedToPlayerOnServer1()
    {
        await using WebApplicationFactory<Program> server2 = StartServer2();
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_server1);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_server1);

        await using HubConnection aliceOnServer1 = await TestHelper.ConnectToHubAsync(_server1, alice);
        Task<OnlinePlayer> bobCameOnline = TestHelper.WaitForEventAsync<OnlinePlayer>(
            aliceOnServer1, nameof(IGameClient.PlayerOnline), p => p.PlayerId == bob.PlayerId);

        await using HubConnection bobOnServer2 = await TestHelper.ConnectToHubAsync(server2, bob);

        await bobCameOnline.WaitAsync(Timeout);

        // Danh sách online đọc từ Redis nên server nào trả lời cũng giống nhau.
        OnlinePlayersResponse fromServer2 = await bobOnServer2.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        Assert.Contains(fromServer2.Players, p => p.PlayerId == alice.PlayerId);
        Assert.Contains(fromServer2.Players, p => p.PlayerId == bob.PlayerId);
    }
}
