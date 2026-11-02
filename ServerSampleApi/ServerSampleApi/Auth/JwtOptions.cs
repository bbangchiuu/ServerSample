using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ServerSampleApi.Auth
{
    /// <summary>
    /// Cấu hình JWT, đọc từ nhóm "Jwt" (Options pattern, giống ServerOptions ở giai đoạn 1).
    ///  - Issuer, Audience, AccessTokenMinutes: nằm trong appsettings.json (không bí mật).
    ///  - SigningKey: BÍ MẬT, KHÔNG nằm trong appsettings. Khi dev thì nằm trong User Secrets
    ///    (dotnet user-secrets set "Jwt:SigningKey" "..."), khi chạy thật thì đặt bằng biến môi trường Jwt__SigningKey.
    /// </summary>
    public class JwtOptions
    {
        public const string SectionName = "Jwt";

        /// <summary>"Ai phát hành token": tên server này. Server kiểm tra token có đúng do mình phát hành không.</summary>
        [Required]
        public string Issuer { get; set; } = "";

        /// <summary>"Token dành cho ai": client game. Token phát cho dịch vụ khác thì không dùng được ở đây.</summary>
        [Required]
        public string Audience { get; set; } = "";

        /// <summary>
        /// Khoá bí mật dùng để KÝ token. Ai có khoá này là tự tạo được token giả mạo bất kỳ người chơi nào,
        /// nên tuyệt đối không commit lên Git. Thuật toán HS256 cần khoá dài ít nhất 256 bit (32 byte).
        /// </summary>
        [Required]
        [MinLength(32)]
        public string SigningKey { get; set; } = "";

        /// <summary>Token hết hạn sau bao nhiêu phút. Hết hạn thì phải đăng nhập lại.</summary>
        [Range(1, 24 * 60)]
        public int AccessTokenMinutes { get; set; } = 60;

        /// <summary>
        /// Khoá dùng cho HS256. Dùng CHUNG ở 2 nơi: TokenService (ký token) và JwtBearer trong Program.cs
        /// (kiểm tra token), nên viết một chỗ để hai bên chắc chắn giống nhau.
        /// </summary>
        public SymmetricSecurityKey CreateSigningKey() => new(Encoding.UTF8.GetBytes(SigningKey));
    }
}
