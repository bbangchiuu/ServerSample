using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServerSampleApi.Models;

namespace ServerSampleApi.Auth
{
    /// <summary>Token vừa tạo và thời điểm nó hết hạn.</summary>
    public record AccessToken(string Token, DateTime ExpiresAt);

    /// <summary>
    /// Tạo JWT (JSON Web Token) cho người chơi sau khi đăng nhập hoặc đăng ký thành công.
    ///
    /// JWT gồm 3 phần, nối bằng dấu chấm:  HEADER.PAYLOAD.SIGNATURE
    ///  - Header:    thuật toán ký, ví dụ {"alg":"HS256","typ":"JWT"}
    ///  - Payload:   các "claim" (thông tin về người chơi), ví dụ {"sub":"&lt;playerId&gt;","exp":1767225600,...}
    ///  - Signature: chữ ký = HMAC-SHA256(base64url(header) + "." + base64url(payload), SigningKey)
    /// Header và payload chỉ là JSON mã hoá Base64Url (Base64 thay "+/" bằng "-_", bỏ "="), AI CŨNG ĐỌC ĐƯỢC,
    /// không phải mã hoá bí mật.
    /// Thứ bảo vệ token là chữ ký: sửa dù chỉ một ký tự trong payload (ví dụ đổi "sub" thành người khác)
    /// thì chữ ký không còn khớp, và không có SigningKey thì không ký lại được.
    ///
    /// Singleton: không giữ dữ liệu riêng của request nào, cả server dùng chung một object.
    /// </summary>
    public class TokenService
    {
        private readonly JwtOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly SigningCredentials _signingCredentials;
        private readonly JsonWebTokenHandler _tokenHandler = new();

        /// <summary>
        /// timeProvider: "đồng hồ" để biết bây giờ là mấy giờ. Không gọi thẳng DateTime.UtcNow, vì như vậy giờ luôn là
        /// giờ thật và mỗi lần chạy mỗi khác: test không thể kiểm tra "exp" đúng bằng bao nhiêu.
        ///  - Chạy thật: DI truyền TimeProvider.System (đồng hồ của máy, đăng ký trong Program.cs).
        ///  - Unit test: truyền một đồng hồ GIẢ, đặt giờ tuỳ ý (xem TokenServiceTests).
        /// </summary>
        public TokenService(IOptions<JwtOptions> options, TimeProvider timeProvider)
        {
            _options = options.Value;
            _timeProvider = timeProvider;

            // HS256 = HMAC-SHA256: dùng CÙNG một khoá để ký (lúc tạo token) và để kiểm tra (lúc nhận token).
            _signingCredentials = new SigningCredentials(_options.CreateSigningKey(), SecurityAlgorithms.HmacSha256);
        }

        public AccessToken CreateAccessToken(AccountModel account)
        {
            // GetUtcNow() trả DateTimeOffset; .UtcDateTime đổi sang DateTime (Kind = Utc) như code cũ dùng.
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            DateTime expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = _options.Issuer,     // claim "iss"
                Audience = _options.Audience, // claim "aud"
                IssuedAt = now,               // claim "iat": tạo lúc nào
                NotBefore = now,              // claim "nbf": chưa tới lúc này thì chưa dùng được
                Expires = expiresAt,          // claim "exp": hết hạn lúc nào
                Subject = new ClaimsIdentity(new[]
                {
                    // "sub" (subject): token này nói về AI. Account.Id cũng chính là PlayerId.
                    // Server lấy playerId từ đây (User.GetPlayerId()), không tin playerId client gửi lên.
                    new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
                    new Claim(JwtRegisteredClaimNames.UniqueName, account.Username),
                    // "jti" (JWT ID): mã riêng của từng token, dùng sau này khi cần thu hồi một token cụ thể.
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                }),
                SigningCredentials = _signingCredentials,
            };

            return new AccessToken(_tokenHandler.CreateToken(descriptor), expiresAt);
        }
    }
}
