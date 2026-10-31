using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>Các hàm dùng chung cho mọi test.</summary>
internal static class TestHelper
{
    public const string Password = "secret123";

    /// <summary>
    /// Tên tài khoản ngẫu nhiên, hợp lệ (3-20 ký tự, chữ/số/_).
    /// Các test trong cùng một class dùng chung một server, nên mỗi test phải tự tạo tên riêng
    /// để không đụng tài khoản của test khác.
    /// </summary>
    public static string NewUsername() => "u_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Đăng ký một tài khoản mới và trả về playerId của nó.</summary>
    public static async Task<Guid> RegisterNewPlayerAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = NewUsername(), Password = Password });
        response.EnsureSuccessStatusCode(); // không thành công thì ném exception, test fail ngay tại đây

        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.PlayerId;
    }

    /// <summary>
    /// Giả lập "đã sang ngày mới": sửa thẳng trong database thành "lần nhận thưởng gần nhất là hôm qua",
    /// để người chơi nhận thưởng hằng ngày thêm được một lần nữa.
    /// Lấy GameDbContext từ chính server ảo của test (cùng database với API).
    /// </summary>
    public static async Task SetLastDailyRewardToYesterdayAsync(ApiFactory factory, Guid playerId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        DateOnly yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        await db.Players
            .Where(p => p.Id == playerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.LastDailyRewardDate, yesterday));
    }

    /// <summary>
    /// Sửa thẳng số vàng trong PostgreSQL, KHÔNG đi qua API nên Redis không biết gì.
    /// Giả lập người chơi đã có vàng từ trước khi có bảng xếp hạng.
    /// </summary>
    public static async Task SetGoldInDatabaseAsync(ApiFactory factory, Guid playerId, long gold)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        await db.Players
            .Where(p => p.Id == playerId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.Gold, gold));
    }
}
