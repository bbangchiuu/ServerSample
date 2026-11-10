using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;

namespace ServerSampleApi.Tests;

/// <summary>Test phần realtime của clan: chat clan, báo khi có người vào / rời clan.</summary>
public class ClanRealtimeTests : IClassFixture<ApiFactory>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Chờ thêm chừng này để chắc một tin KHÔNG đến (không chỉ là đến muộn).</summary>
    private static readonly TimeSpan NotReceivedDelay = TimeSpan.FromMilliseconds(300);

    private readonly ApiFactory _factory;

    public ClanRealtimeTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ClanChat_ReachesClanMembersOnly()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer outsider = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice);
        await JoinAsync(bob, clan.Id);

        await using HubConnection aliceHub = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection bobHub = await TestHelper.ConnectToHubAsync(_factory, bob);
        await using HubConnection outsiderHub = await TestHelper.ConnectToHubAsync(_factory, outsider);
        Task<ChatMessage> aliceReceived = WaitForClanChat(aliceHub);
        Task<ChatMessage> bobReceived = WaitForClanChat(bobHub);
        Task<ChatMessage> outsiderReceived = WaitForClanChat(outsiderHub);

        await aliceHub.InvokeAsync(nameof(GameHub.SendClanChatMessage), "  clan only  ");

        ChatMessage message = await bobReceived.WaitAsync(Timeout);
        Assert.Equal(alice.PlayerId, message.PlayerId);
        Assert.Equal("clan only", message.Text);
        await aliceReceived.WaitAsync(Timeout); // người gửi cũng nhận lại tin của mình

        await Task.Delay(NotReceivedDelay);
        Assert.False(outsiderReceived.IsCompleted);
    }

    [Fact]
    public async Task ClanChat_WhenNotInClan_ThrowsHubException()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        await using HubConnection aliceHub = await TestHelper.ConnectToHubAsync(_factory, alice);

        HubException error = await Assert.ThrowsAsync<HubException>(
            () => aliceHub.InvokeAsync(nameof(GameHub.SendClanChatMessage), "hello"));

        Assert.Contains("not in a clan", error.Message);
    }

    [Fact]
    public async Task ClanChat_AfterLeaving_IsNoLongerReceived_EvenOnAlreadyOpenConnection()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice);
        await JoinAsync(bob, clan.Id);

        // Bob mở kết nối khi còn trong clan, rồi rời clan qua HTTP mà KHÔNG kết nối lại.
        await using HubConnection aliceHub = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection bobHub = await TestHelper.ConnectToHubAsync(_factory, bob);
        (await bob.Client.PostAsync("/api/clans/me/leave", null)).EnsureSuccessStatusCode();
        Task<ChatMessage> bobReceived = WaitForClanChat(bobHub);
        Task<ChatMessage> aliceReceived = WaitForClanChat(aliceHub);

        await aliceHub.InvokeAsync(nameof(GameHub.SendClanChatMessage), "bob is gone");

        await aliceReceived.WaitAsync(Timeout);
        await Task.Delay(NotReceivedDelay);
        Assert.False(bobReceived.IsCompleted); // danh sách người nhận đọc từ database mỗi lần gửi
    }

    [Fact]
    public async Task JoinClan_NotifiesExistingMembersAndJoinersOtherDevices()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice);

        await using HubConnection aliceHub = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection bobPc = await TestHelper.ConnectToHubAsync(_factory, bob);
        Task<ClanMemberEvent> aliceReceived = WaitForMemberEvent(aliceHub, nameof(IGameClient.ClanMemberJoined), bob.PlayerId);
        Task<ClanMemberEvent> bobPcReceived = WaitForMemberEvent(bobPc, nameof(IGameClient.ClanMemberJoined), bob.PlayerId);

        // Bob vào clan trên "điện thoại" (HTTP).
        await JoinAsync(bob, clan.Id);

        ClanMemberEvent joined = await aliceReceived.WaitAsync(Timeout);
        Assert.Equal(clan.Id, joined.ClanId);
        Assert.Equal(2, joined.MemberCount);
        Assert.Equal(alice.PlayerId, joined.LeaderId);
        Assert.False(string.IsNullOrEmpty(joined.DisplayName));
        await bobPcReceived.WaitAsync(Timeout); // "máy tính" của Bob cũng biết Bob vừa vào clan
    }

    [Fact]
    public async Task LeaderLeaves_RemainingMembersAndLeaverAreNotified_WithNewLeader()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice);
        await JoinAsync(bob, clan.Id);

        await using HubConnection alicePc = await TestHelper.ConnectToHubAsync(_factory, alice);
        await using HubConnection bobHub = await TestHelper.ConnectToHubAsync(_factory, bob);
        Task<ClanMemberEvent> bobReceived = WaitForMemberEvent(bobHub, nameof(IGameClient.ClanMemberLeft), alice.PlayerId);
        Task<ClanMemberEvent> alicePcReceived = WaitForMemberEvent(alicePc, nameof(IGameClient.ClanMemberLeft), alice.PlayerId);

        (await alice.Client.PostAsync("/api/clans/me/leave", null)).EnsureSuccessStatusCode();

        ClanMemberEvent left = await bobReceived.WaitAsync(Timeout);
        Assert.Equal(bob.PlayerId, left.LeaderId); // Bob thành chủ clan mới
        Assert.Equal(1, left.MemberCount);
        await alicePcReceived.WaitAsync(Timeout);
    }

    [Fact]
    public async Task LastMemberLeaves_EventSaysClanIsGone()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        await CreateClanAsync(alice);
        await using HubConnection alicePc = await TestHelper.ConnectToHubAsync(_factory, alice);
        Task<ClanMemberEvent> received = WaitForMemberEvent(alicePc, nameof(IGameClient.ClanMemberLeft), alice.PlayerId);

        (await alice.Client.PostAsync("/api/clans/me/leave", null)).EnsureSuccessStatusCode();

        ClanMemberEvent left = await received.WaitAsync(Timeout);
        Assert.Null(left.LeaderId);
        Assert.Equal(0, left.MemberCount);
    }

    private static Task<ChatMessage> WaitForClanChat(HubConnection connection) =>
        TestHelper.WaitForEventAsync<ChatMessage>(connection, nameof(IGameClient.ReceiveClanChatMessage));

    private static Task<ClanMemberEvent> WaitForMemberEvent(HubConnection connection, string methodName, Guid playerId) =>
        TestHelper.WaitForEventAsync<ClanMemberEvent>(connection, methodName, e => e.PlayerId == playerId);

    private static async Task<ClanResponse> CreateClanAsync(TestPlayer player)
    {
        HttpResponseMessage response = await player.Client.PostAsJsonAsync("/api/clans",
            new CreateClanRequest { Name = "c_" + Guid.NewGuid().ToString("N")[..12] });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClanResponse>())!;
    }

    private static async Task JoinAsync(TestPlayer player, Guid clanId)
    {
        (await player.Client.PostAsync($"/api/clans/{clanId}/join", null)).EnsureSuccessStatusCode();
    }
}
