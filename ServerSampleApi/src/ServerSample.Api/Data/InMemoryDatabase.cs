using System.Collections.Concurrent;

namespace ServerSample.Api.Data;

/// <summary>
/// "Database" tạm giữ trong RAM: tắt server là mất hết. Giai đoạn 3 thay bằng PostgreSQL (EF Core).
///
/// Đăng ký Singleton: cả server dùng chung một instance, và nhiều request chạy song song trên
/// nhiều thread cùng đọc/ghi, nên dùng ConcurrentDictionary.
/// </summary>
public sealed class InMemoryDatabase
{
    /// <summary>Key là username, không phân biệt hoa thường: "Bang" và "bang" là cùng một tài khoản.</summary>
    public ConcurrentDictionary<string, Account> AccountsByUsername { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentDictionary<Guid, Player> Players { get; } = new();
}
