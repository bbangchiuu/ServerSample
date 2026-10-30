using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Controllers;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Test API người chơi (PlayersController): profile, đổi tên, thưởng hằng ngày.</summary>
public class PlayersApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public PlayersApiTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task NewPlayer_HasDefaultProfile()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");

        Assert.Equal(1, profile!.Level);
        Assert.Equal(0, profile.Gold);
        Assert.Null(profile.LastDailyRewardDate);
    }

    [Fact]
    public async Task UnknownPlayer_ReturnsNotFound()
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/players/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ChangeDisplayName_TrimsSpaces_AndIsSaved()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage response = await _client.PutAsJsonAsync($"/api/players/{playerId}/display-name",
            new ChangeDisplayNameRequest { DisplayName = "  Hiep Si  " });
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hiep Si", profile!.DisplayName);
    }

    [Fact]
    public async Task ChangeDisplayName_TooShortAfterTrimming_ReturnsBadRequest()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        // 8 ký tự nên qua được [StringLength], nhưng bỏ khoảng trắng chỉ còn "ab".
        HttpResponseMessage response = await _client.PutAsJsonAsync($"/api/players/{playerId}/display-name",
            new ChangeDisplayNameRequest { DisplayName = "   ab   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DailyReward_CanBeClaimedOnlyOncePerDay()
    {
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        HttpResponseMessage first = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);
        HttpResponseMessage second = await _client.PostAsync($"/api/players/{playerId}/daily-reward", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_20RequestsAtTheSameTime_OnlyOneSucceeds()
    {
        // Giả lập người chơi gian lận: bắn 20 request "nhận thưởng" cùng một lúc.
        // Thử bằng tay rất khó làm được; test thì làm được, và lần nào chạy cũng kiểm tra lại.
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);

        // Tạo 20 Task cùng chạy, rồi chờ tất cả xong.
        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync($"/api/players/{playerId}/daily-reward", null))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        // Đúng 1 request thành công, 19 request còn lại bị từ chối ĐÀNG HOÀNG (409, không phải lỗi 500),
        // và chỉ được cộng vàng 1 lần.
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(PlayersController.DailyRewardGold, profile!.Gold);
    }

    [Fact]
    public async Task DailyReward_SecondDay_20RequestsAtTheSameTime_OnlyOneSucceeds()
    {
        // Ngày thứ hai khác ngày đầu: dòng potion ĐÃ CÓ trong túi, nên không còn khoá chính nào
        // "vô tình" chặn hộ. Đây là chỗ bước 3 để lọt lỗi: cả 20 request đều báo thành công.
        Guid playerId = await TestHelper.RegisterNewPlayerAsync(_client);
        await _client.PostAsync($"/api/players/{playerId}/daily-reward", null); // ngày 1: 100 vàng, 1 potion

        // Giả lập "đã sang ngày mới": sửa thẳng trong database thành "lần nhận gần nhất là hôm qua".
        // Lấy GameDbContext từ chính server ảo của test (cùng database với API).
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
            await db.Players
                .Where(p => p.Id == playerId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastDailyRewardDate, yesterday));
        }

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => _client.PostAsync($"/api/players/{playerId}/daily-reward", null)));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(19, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        PlayerProfileResponse? profile = await _client.GetFromJsonAsync<PlayerProfileResponse>($"/api/players/{playerId}");
        Assert.Equal(2 * PlayersController.DailyRewardGold, profile!.Gold); // đúng 2 lần thưởng: 200 vàng
        InventoryResponse? inventory = await _client.GetFromJsonAsync<InventoryResponse>($"/api/players/{playerId}/inventory");
        Assert.Equal(2, Assert.Single(inventory!.Items).Quantity);           // đúng 2 potion
    }
}
