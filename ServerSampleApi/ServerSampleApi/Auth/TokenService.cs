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
    ///  - Signature: chữ ký = HMAC-SHA256(header + payload, SigningKey)
    /// Header và payload chỉ là JSON mã hoá Base64, AI CŨNG ĐỌC ĐƯỢC, không phải mã hoá bí mật.
    /// Thứ bảo vệ token là chữ ký: sửa dù chỉ một ký tự trong payload (ví dụ đổi "sub" thành người khác)
    /// thì chữ ký không còn khớp, và không có SigningKey thì không ký lại được.
    ///
    /// Singleton: không giữ dữ liệu riêng của request nào, cả server dùng chung một object.
    /// </summary>
    public class TokenService
    {
        private readonly JwtOptions _options;
        private readonly SigningCredentials _signingCredentials;
        private readonly JsonWebTokenHandler _tokenHandler = new();

        public TokenService(IOptions<JwtOptions> options)
        {
            _options = options.Value;

            // HS256 = HMAC-SHA256: dùng CÙNG một khoá để ký (lúc tạo token) và để kiểm tra (lúc nhận token).
            _signingCredentials = new SigningCredentials(_options.CreateSigningKey(), SecurityAlgorithms.HmacSha256);
        }

        public AccessToken CreateAccessToken(AccountModel account)
        {
            DateTime now = DateTime.UtcNow;
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
                    // Bước 2 server sẽ lấy playerId từ đây, thay vì tin playerId client gửi trên URL.
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
