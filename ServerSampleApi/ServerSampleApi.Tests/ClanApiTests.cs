using System.Net;
using System.Net.Http.Json;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API clan: tạo, tìm, vào, rời, chuyển chủ clan, clan đầy.</summary>
public class ClanApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ClanApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateClan_CreatorBecomesLeaderAndOnlyMember()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        string name = NewClanName();

        HttpResponseMessage response = await alice.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = "  " + name + "  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        ClanResponse? clan = await response.Content.ReadFromJsonAsync<ClanResponse>();
        Assert.Equal(name, clan!.Name); // khoảng trắng hai đầu đã bị bỏ
        Assert.Equal(alice.PlayerId, clan.LeaderId);
        Assert.Equal(1, clan.MemberCount);
        Assert.Equal(ClansController.MaxMembers, clan.MaxMembers);
        ClanMemberResponse member = Assert.Single(clan.Members);
        Assert.Equal(alice.PlayerId, member.PlayerId);
        Assert.True(member.IsLeader);

        // Header Location chỉ tới GET /api/clans/{id}; /api/clans/me cũng trả về đúng clan đó.
        Assert.Equal($"/api/clans/{clan.Id}", response.Headers.Location!.AbsolutePath, ignoreCase: true);
        ClanResponse? mine = await alice.Client.GetFromJsonAsync<ClanResponse>("/api/clans/me");
        Assert.Equal(clan.Id, mine!.Id);
    }

    [Fact]
    public async Task CreateClan_NameTakenIgnoringCase_Returns409()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        string name = NewClanName();
        await CreateClanAsync(alice, name);

        HttpResponseMessage response = await bob.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = name.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("name already taken", await response.Content.ReadAsStringAsync());
        // Bob không bị kẹt trong một clan "nửa vời": vẫn chưa ở clan nào.
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync("/api/clans/me")).StatusCode);
    }

    [Fact]
    public async Task CreateClan_WhenAlreadyInClan_Returns409()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        await CreateClanAsync(alice, NewClanName());

        HttpResponseMessage response = await alice.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = NewClanName() });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Already in a clan", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("   ab   ")] // đủ 3 ký tự nhờ khoảng trắng, nhưng bỏ khoảng trắng đi chỉ còn 2
    [InlineData("a_name_that_is_way_too_long")]
    public async Task CreateClan_InvalidName_Returns400(string name)
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage response = await alice.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchClans_FindsByPartOfNameIgnoringCase()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        string unique = Guid.NewGuid().ToString("N")[..8];
        await CreateClanAsync(alice, "Dragon" + unique);

        ClanListResponse? result = await alice.Client.GetFromJsonAsync<ClanListResponse>($"/api/clans?search=GON{unique.ToUpperInvariant()}");

        ClanSummaryResponse clan = Assert.Single(result!.Clans);
        Assert.Equal("Dragon" + unique, clan.Name);
        Assert.Equal(1, clan.MemberCount);
    }

    [Fact]
    public async Task SearchClans_PercentSignIsLiteral()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        await CreateClanAsync(alice, NewClanName());

        // "%" không được hiểu là "khớp mọi thứ": không clan nào có ký tự % trong tên.
        ClanListResponse? result = await alice.Client.GetFromJsonAsync<ClanListResponse>("/api/clans?search=%25");

        Assert.Empty(result!.Clans);
    }

    [Fact]
    public async Task JoinClan_AddsMember()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice, NewClanName());

        HttpResponseMessage response = await bob.Client.PostAsync($"/api/clans/{clan.Id}/join", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ClanResponse? joined = await response.Content.ReadFromJsonAsync<ClanResponse>();
        Assert.Equal(2, joined!.MemberCount);
        Assert.Equal(new[] { alice.PlayerId, bob.PlayerId }, joined.Members.Select(m => m.PlayerId)); // ai vào trước đứng trước
        Assert.Equal(alice.PlayerId, joined.LeaderId);
    }

    [Fact]
    public async Task JoinClan_WhenAlreadyInClan_Returns409_AndDoesNotTakeASlot()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse aliceClan = await CreateClanAsync(alice, NewClanName());
        await CreateClanAsync(bob, NewClanName());

        HttpResponseMessage response = await bob.Client.PostAsync($"/api/clans/{aliceClan.Id}/join", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // Chỗ đã giữ (+1) được trả lại khi transaction bị huỷ.
        ClanResponse? clan = await alice.Client.GetFromJsonAsync<ClanResponse>($"/api/clans/{aliceClan.Id}");
        Assert.Equal(1, clan!.MemberCount);
    }

    [Fact]
    public async Task JoinClan_UnknownClan_Returns404()
    {
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage response = await bob.Client.PostAsync($"/api/clans/{Guid.NewGuid()}/join", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JoinClan_TwoPlayersRaceForLastSlot_OnlyOneGetsIn()
    {
        TestPlayer leader = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(leader, NewClanName());

        // Lấp đầy clan, chỉ chừa MỘT chỗ trống.
        TestPlayer[] fillers = await Task.WhenAll(Enumerable.Range(0, ClansController.MaxMembers - 2)
            .Select(_ => TestHelper.RegisterNewPlayerAsync(_factory)));
        foreach (TestPlayer filler in fillers)
        {
            (await filler.Client.PostAsync($"/api/clans/{clan.Id}/join", null)).EnsureSuccessStatusCode();
        }

        // Hai người cùng lúc tranh chỗ cuối cùng.
        TestPlayer first = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer second = await TestHelper.RegisterNewPlayerAsync(_factory);
        HttpResponseMessage[] responses = await Task.WhenAll(
            first.Client.PostAsync($"/api/clans/{clan.Id}/join", null),
            second.Client.PostAsync($"/api/clans/{clan.Id}/join", null));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        ClanResponse? full = await leader.Client.GetFromJsonAsync<ClanResponse>($"/api/clans/{clan.Id}");
        Assert.Equal(ClansController.MaxMembers, full!.MemberCount);
        Assert.Equal(ClansController.MaxMembers, full.Members.Count);
    }

    [Fact]
    public async Task LeaveClan_MemberLeaves_CountDecreases()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice, NewClanName());
        await bob.Client.PostAsync($"/api/clans/{clan.Id}/join", null);

        HttpResponseMessage response = await bob.Client.PostAsync("/api/clans/me/leave", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        ClanResponse? after = await alice.Client.GetFromJsonAsync<ClanResponse>($"/api/clans/{clan.Id}");
        Assert.Equal(1, after!.MemberCount);
        Assert.Equal(alice.PlayerId, after.LeaderId);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Client.GetAsync("/api/clans/me")).StatusCode);
    }

    [Fact]
    public async Task LeaveClan_LeaderLeaves_EarliestMemberBecomesLeader()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer carol = await TestHelper.RegisterNewPlayerAsync(_factory);
        ClanResponse clan = await CreateClanAsync(alice, NewClanName());
        await bob.Client.PostAsync($"/api/clans/{clan.Id}/join", null);
        await carol.Client.PostAsync($"/api/clans/{clan.Id}/join", null);

        await alice.Client.PostAsync("/api/clans/me/leave", null);

        ClanResponse? after = await bob.Client.GetFromJsonAsync<ClanResponse>($"/api/clans/{clan.Id}");
        Assert.Equal(bob.PlayerId, after!.LeaderId); // Bob vào trước Carol
        Assert.True(after.Members.Single(m => m.PlayerId == bob.PlayerId).IsLeader);
        Assert.Equal(2, after.MemberCount);
    }

    [Fact]
    public async Task LeaveClan_LastMemberLeaves_ClanIsDeletedAndNameIsFreeAgain()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);
        TestPlayer bob = await TestHelper.RegisterNewPlayerAsync(_factory);
        string name = NewClanName();
        ClanResponse clan = await CreateClanAsync(alice, name);

        await alice.Client.PostAsync("/api/clans/me/leave", null);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.Client.GetAsync($"/api/clans/{clan.Id}")).StatusCode);
        HttpResponseMessage reuse = await bob.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = name });
        Assert.Equal(HttpStatusCode.Created, reuse.StatusCode);
    }

    [Fact]
    public async Task LeaveClan_WhenNotInClan_Returns404()
    {
        TestPlayer alice = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage response = await alice.Client.PostAsync("/api/clans/me/leave", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ClanApis_WithoutToken_Return401()
    {
        HttpClient anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/clans")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/clans/me/leave", null)).StatusCode);
    }

    /// <summary>Tên clan ngẫu nhiên (các test dùng chung một database nên tên không được trùng nhau).</summary>
    private static string NewClanName() => "c_" + Guid.NewGuid().ToString("N")[..12];

    private static async Task<ClanResponse> CreateClanAsync(TestPlayer player, string name)
    {
        HttpResponseMessage response = await player.Client.PostAsJsonAsync("/api/clans", new CreateClanRequest { Name = name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClanResponse>())!;
    }
}
