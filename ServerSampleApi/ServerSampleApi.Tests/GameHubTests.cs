using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test kênh realtime /hubs/game. Mỗi người chơi trong test mở một kết nối SignalR thật (WebSocket) tới server ảo,
/// giống hệt cách game sẽ kết nối.
/// </summary>
public class GameHubTests : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ApiFactory _factory;

    public GameHubTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Connect_WithoutToken_IsRejected()
    {
        await using HubConnection connection = CreateConnection(accessToken: null);

        // Hub có [Authorize]: server từ chối mở WebSocket (401), StartAsync ném lỗi.
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task SendChatMessage_EveryConnectedPlayerReceivesIt()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        await alice.Client.PutAsJsonAsync("/api/players/me/display-name", new ChangeDisplayNameRequest { DisplayName = "Alice" });

        await using HubConnection aliceConnection = await ConnectAsync(alice);
        await using HubConnection bobConnection = await ConnectAsync(bob);
        Task<ChatMessage> aliceReceived = WaitForChatMessageAsync(aliceConnection);
        Task<ChatMessage> bobReceived = WaitForChatMessageAsync(bobConnection);

        // Alice gọi hàm của server; server gọi hàm ReceiveChatMessage của MỌI client.
        await aliceConnection.InvokeAsync(nameof(GameHub.SendChatMessage), "  hello  ");

        ChatMessage messageForBob = await bobReceived.WaitAsync(Timeout);
        ChatMessage messageForAlice = await aliceReceived.WaitAsync(Timeout); // người gửi cũng nhận được
        Assert.Equal("hello", messageForBob.Text); // server đã cắt dấu cách thừa
        Assert.Equal(alice.PlayerId, messageForBob.PlayerId); // người gửi lấy từ token
        Assert.Equal("Alice", messageForBob.DisplayName); // tên hiển thị đọc từ database
        Assert.Equal(messageForBob.SentAt, messageForAlice.SentAt);
    }

    [Fact]
    public async Task MessagePackClient_AndJsonClient_ChatTogether()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Alice dùng MessagePack (nhị phân), Bob dùng JSON. Server hỗ trợ cả hai: mỗi kết nối tự chọn lúc handshake,
        // server tự chuyển tin sang đúng định dạng của từng người nhận.
        await using HubConnection aliceMessagePack = await TestHelper.ConnectToHubAsync(_factory, alice, useMessagePack: true);
        await using HubConnection bobJson = await ConnectAsync(bob);
        Task<ChatMessage> bobReceived = WaitForChatMessageAsync(bobJson);
        Task<ChatMessage> aliceReceived = WaitForChatMessageAsync(aliceMessagePack);

        await aliceMessagePack.InvokeAsync(nameof(GameHub.SendChatMessage), "sent as messagepack");

        ChatMessage forBob = await bobReceived.WaitAsync(Timeout);
        ChatMessage forAlice = await aliceReceived.WaitAsync(Timeout);
        Assert.Equal("sent as messagepack", forBob.Text);
        Assert.Equal(alice.PlayerId, forAlice.PlayerId);
        Assert.Equal("sent as messagepack", forAlice.Text);

        // Hàm có giá trị trả về (Completion) cũng chạy được qua MessagePack.
        OnlinePlayersResponse online = await aliceMessagePack.InvokeAsync<OnlinePlayersResponse>(nameof(GameHub.GetOnlinePlayers));
        Assert.Contains(online.Players, p => p.PlayerId == bob.PlayerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SendChatMessage_EmptyText_ThrowsHubException(string? text)
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);
        await using HubConnection connection = await ConnectAsync(player);

        HubException error = await Assert.ThrowsAsync<HubException>(
            () => connection.InvokeAsync(nameof(GameHub.SendChatMessage), text));

        Assert.Contains("1-200 characters", error.Message);
    }

    [Fact]
    public async Task SendChatMessage_TooLong_ThrowsHubException()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);
        await using HubConnection connection = await ConnectAsync(player);

        string tooLong = new('a', GameHub.MaxChatMessageLength + 1);

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync(nameof(GameHub.SendChatMessage), tooLong));
    }

    private Task<HubConnection> ConnectAsync(TestPlayer player) => TestHelper.ConnectToHubAsync(_factory, player);

    private HubConnection CreateConnection(string? accessToken) => TestHelper.CreateHubConnection(_factory, accessToken);

    /// <summary>Đăng ký nghe hàm ReceiveChatMessage, trả về Task hoàn thành khi nhận được tin nhắn đầu tiên.</summary>
    private static Task<ChatMessage> WaitForChatMessageAsync(HubConnection connection)
    {
        var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<ChatMessage>(nameof(IGameClient.ReceiveChatMessage), message => received.TrySetResult(message));
        return received.Task;
    }
}
