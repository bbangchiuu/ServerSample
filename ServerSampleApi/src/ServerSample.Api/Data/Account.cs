namespace ServerSample.Api.Data;

/// <summary>Thông tin đăng nhập. Tách riêng khỏi Player: dữ liệu bảo mật không nằm chung với dữ liệu game.</summary>
public sealed class Account
{
    public required Guid Id { get; init; }

    public required string Username { get; init; }

    /// <summary>Không bao giờ lưu mật khẩu gốc, chỉ lưu bản băm (hash) do PasswordHasher tạo ra.</summary>
    public string PasswordHash { get; set; } = "";

    public required DateTimeOffset CreatedAt { get; init; }
}
