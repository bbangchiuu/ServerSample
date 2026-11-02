using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;

namespace ServerSampleApi.Tests;

/// <summary>
/// Một người chơi vừa đăng ký trong test.
/// Client đã gắn sẵn header "Authorization: Bearer &lt;token&gt;", gọi API nào cũng là gọi với tư cách người này.
/// </summary>
internal record TestPlayer(Guid PlayerId, HttpClient Client);

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

    /// <summary>
    /// Đăng ký một tài khoản mới, rồi trả về playerId và một HttpClient MỚI đã gắn token của người đó.
    /// Mỗi người chơi một client riêng, nên trong cùng một test có thể có nhiều người chơi khác nhau.
    /// </summary>
    public static async Task<TestPlayer> RegisterNewPlayerAsync(ApiFactory factory)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/accounts/register",
            new RegisterRequest { Username = NewUsername(), Password = Password });
        response.EnsureSuccessStatusCode(); // không thành công thì ném exception, test fail ngay tại đây

        AccountResponse? account = await response.Content.ReadFromJsonAsync<AccountResponse>();

        // Từ giờ mọi request của client này đều kèm: Authorization: Bearer eyJhbGciOi...
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account!.AccessToken);
        return new TestPlayer(account.PlayerId, client);
    }

    /// <summary>
    /// Tự ký một token bất kỳ, để thử các trường hợp xấu: token hết hạn, token ký bằng khoá giả...
    /// Mặc định dùng đúng khoá, issuer, audience của server test.
    /// </summary>
    public static string CreateToken(Guid playerId, DateTime expiresAt, string signingKey = ApiFactory.JwtSigningKey)
    {
        DateTime issuedAt = expiresAt.AddHours(-1);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ServerSampleApi",
            Audience = "ServerSampleUnity",
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expiresAt,
            Subject = new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, playerId.ToString()) }),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256),
        });
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
