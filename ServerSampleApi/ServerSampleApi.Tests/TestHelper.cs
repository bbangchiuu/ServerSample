using System.Net.Http.Json;
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
}
