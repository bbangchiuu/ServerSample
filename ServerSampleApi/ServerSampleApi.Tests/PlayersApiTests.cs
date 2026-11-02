using System.Net;
using System.Net.Http.Json;
using ServerSampleApi.Controllers;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API người chơi (PlayersController): profile, đổi tên, thưởng hằng ngày.</summary>
public class PlayersApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PlayersApiTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task NewPlayer_HasDefaultProfile()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        PlayerProfileResponse? profile = await player.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");

        Assert.Equal(player.PlayerId, profile!.Id);
        Assert.Equal(1, profile.Level);
        Assert.Equal(0, profile.Gold);
        Assert.Null(profile.LastDailyRewardDate);
    }

    [Fact]
    public async Task ChangeDisplayName_TrimsSpaces_AndIsSaved()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage response = await player.Client.PutAsJsonAsync("/api/players/me/display-name",
            new ChangeDisplayNameRequest { DisplayName = "  Hiep Si  " });
        PlayerProfileResponse? profile = await player.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hiep Si", profile!.DisplayName);
    }

    [Fact]
    public async Task ChangeDisplayName_TooShortAfterTrimming_ReturnsBadRequest()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        // 8 ký tự nên qua được [StringLength], nhưng bỏ khoảng trắng chỉ còn "ab".
        HttpResponseMessage response = await player.Client.PutAsJsonAsync("/api/players/me/display-name",
            new ChangeDisplayNameRequest { DisplayName = "   ab   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DailyReward_CanBeClaimedOnlyOncePerDay()
    {
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        HttpResponseMessage first = await player.Client.PostAsync("/api/players/me/daily-reward", null);
        HttpResponseMessage second = await player.Client.PostAsync("/api/players/me/daily-reward", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        PlayerProfileResponse? profile = await player.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_20RequestsAtTheSameTime_OnlyOneSucceeds()
    {
        // Giả lập người chơi gian lận: bắn 20 request "nhận thưởng" cùng một lúc.
        // Thử bằng tay rất khó làm được; test thì làm được, và lần nào chạy cũng kiểm tra lại.
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);

        // Tạo 20 Task cùng chạy, rồi chờ tất cả xong.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 20)
            .Select(_ => player.Client.PostAsync("/api/players/me/daily-reward", null))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        // Đúng 1 request thành công, 19 request còn lại bị từ chối ĐÀNG HOÀNG (409, không phải lỗi 500),
        // và chỉ được cộng vàng 1 lần.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        PlayerProfileResponse? profile = await player.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_SecondDay_20RequestsAtTheSameTime_OnlyOneSucceeds()
    {
        // Ngày thứ hai khác ngày đầu: dòng potion ĐÃ CÓ trong túi, nên không còn khoá chính nào
        // "vô tình" chặn hộ. Đây là chỗ bước 3 để lọt lỗi: cả 20 request đều báo thành công.
        TestPlayer player = await TestHelper.RegisterNewPlayerAsync(_factory);
        await player.Client.PostAsync("/api/players/me/daily-reward", null); // ngày 1: 100 vàng, 1 potion

        await TestHelper.SetLastDailyRewardToYesterdayAsync(_factory, player.PlayerId);

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => player.Client.PostAsync("/api/players/me/daily-reward", null)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        PlayerProfileResponse? profile = await player.Client.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me");
        Assert.Equal(2 * PlayersController.DailyRewardGold, profile!.Gold); // đúng 2 lần thưởng: 200 vàng
        InventoryResponse? inventory = await player.Client.GetFromJsonAsync<InventoryResponse>("/api/players/me/inventory");
        Assert.Equal(2, Assert.Single(inventory!.Items).Quantity);              // đúng 2 potion
    }
}
