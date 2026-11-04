using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Dtos
{
    /// <summary>Dữ liệu client gửi lên khi làm mới token (/api/accounts/refresh) hoặc đăng xuất (/api/accounts/logout).</summary>
    public class RefreshTokenRequest
    {
        [Required]
        public string RefreshToken { get; set; } = "";
    }
}
