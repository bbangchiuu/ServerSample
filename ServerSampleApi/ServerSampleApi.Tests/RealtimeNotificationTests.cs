using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;

namespace ServerSampleApi.Tests;

/// <summary>
/// Test server TỰ ĐẨY sự kiện xuống client khi có thay đổi qua HTTP API (IHubContext):
///  - ProfileChanged: chỉ gửi cho các kết nối của chính người chơi đó (Clients.User);
///  - LeaderboardChanged: gửi cho tất cả (Clients.All).
/// </summary>
public class RealtimeNotificationTests : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ApiFactory _factory;

    public RealtimeNotificationTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task DailyReward_PushesProfileToEveryDeviceOfThatPlayer_AndNotToOthers()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Alice mở game trên 2 thiết bị (2 kết nối cùng một token), Bob mở trên 1 thiết bị.
        await using HubConnection alicePhone = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection alicePc = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection bobPhone = await TestHelper.ConnectToHubAsync(_factory, bob);
        Task<PlayerProfileResponse> phoneReceived = WaitForAsync<PlayerProfileResponse>(alicePhone, nameof(IGameClient.ProfileChanged));
        Task<PlayerProfileResponse> pcReceived = WaitForAsync<PlayerProfileResponse>(alicePc, nameof(IGameClient.ProfileChanged));
        Task<PlayerProfileResponse> bobReceived = WaitForAsync<PlayerProfileResponse>(bobPhone, nameof(IGameClient.ProfileChanged));

        // Alice nhận thưởng bằng HTTP API (không phải qua hub).
        HttpResponseMessage response = await alice.Client.PostAsync("/api/players/me/daily-reward", null);
        response.EnsureSuccessStatusCode();

        PlayerProfileResponse onPhone = await phoneReceived.WaitAsync(Timeout);
        PlayerProfileResponse onPc = await pcReceived.WaitAsync(Timeout);
        Assert.Equal(alice.PlayerId, onPhone.Id);
        Assert.Equal(PlayersController.DailyRewardGold, onPhone.Gold);
        Assert.Equal(PlayersController.DailyRewardGold, onPc.Gold);

        // Bob không được nhận profile của Alice. Chờ thêm một chút cho chắc tin không đến muộn.
        await Task.Delay(300);
        Assert.False(bobReceived.IsCompleted);
    }

    [Fact]
    public async Task DailyReward_PushesNewRankToEveryone()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        await alice.Client.PutAsJsonAsync("/api/players/me/display-name", new ChangeDisplayNameRequest { DisplayName = "Alice" });

        await using HubConnection bobPhone = await TestHelper.ConnectToHubAsync(_factory, bob);
        Task<LeaderboardEntryResponse> bobReceived = WaitForAsync<LeaderboardEntryResponse>(
            bobPhone, nameof(IGameClient.LeaderboardChanged), entry => entry.PlayerId == alice.PlayerId);

        await alice.Client.PostAsync("/api/players/me/daily-reward", null);

        // Bob (người khác) cũng biết Alice vừa có thêm vàng, kèm hạng mới đọc từ Redis.
        LeaderboardEntryResponse entry = await bobReceived.WaitAsync(Timeout);
        Assert.Equal("Alice", entry.DisplayName);
        Assert.Equal(PlayersController.DailyRewardGold, entry.Gold);
        Assert.True(entry.Rank >= 1);
    }

    [Fact]
    public async Task ChangeDisplayName_PushesProfileToOtherDevicesOfThatPlayer()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        await using HubConnection alicePc = await TestHelper.ConnectToHubAsync(_factory, alice);
        Task<PlayerProfileResponse> pcReceived = WaitForAsync<PlayerProfileResponse>(alicePc, nameof(IGameClient.ProfileChanged));

        // Đổi tên trên "điện thoại" (HTTP), "máy tính" đang mở kết nối hub thấy tên mới ngay.
        await alice.Client.PutAsJsonAsync("/api/players/me/display-name", new ChangeDisplayNameRequest { DisplayName = "New Name" });

        PlayerProfileResponse onPc = await pcReceived.WaitAsync(Timeout);
        Assert.Equal("New Name", onPc.DisplayName);
    }

    /// <summary>Đăng ký nghe một hàm phía client, trả về Task hoàn thành khi nhận được lần đầu (thoả điều kiện nếu có).</summary>
    private static Task<T> WaitForAsync<T>(HubConnection connection, string methodName, Func<T, bool>? condition = null)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T>(methodName, value =>
        {
            if (condition == null || condition(value))
            {
                received.TrySetResult(value);
            }
        });
        return received.Task;
    }
}
