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

        /// <summary>Thời điểm token hết hạn (UTC). Quá thời điểm này thì phải đăng nhập lại.</summary>
        public DateTime AccessTokenExpiresAt { get; set; }
    }
}
