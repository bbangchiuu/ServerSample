namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Dữ liệu trả về cho client sau khi đăng ký hoặc đăng nhập. Chỉ chứa những gì client cần biết,
    /// tách khỏi Account để không bao giờ lộ PasswordHash ra ngoài.
    /// </summary>
    public class AccountResponse
    {
        public Guid PlayerId { get; set; }

        public string Username { get; set; } = "";

        /// <summary>
        /// JWT. Client giữ lại và gửi kèm mọi request sau đó trong header "Authorization: Bearer &lt;token&gt;".
        /// </summary>
        public string AccessToken { get; set; } = "";

        /// <summary>Thời điểm access token hết hạn (UTC). Hết hạn thì dùng RefreshToken để lấy token mới.</summary>
        public DateTime AccessTokenExpiresAt { get; set; }

        /// <summary>
        /// Gửi lên /api/accounts/refresh để lấy access token mới mà không cần nhập lại mật khẩu.
        /// Chỉ dùng được MỘT lần: mỗi lần làm mới, server trả về refresh token mới, client phải thay token cũ bằng token mới.
        /// </summary>
        public string RefreshToken { get; set; } = "";

        /// <summary>Thời điểm refresh token hết hạn (UTC). Hết hạn thì phải đăng nhập lại bằng mật khẩu.</summary>
        public DateTime RefreshTokenExpiresAt { get; set; }
    }
}
