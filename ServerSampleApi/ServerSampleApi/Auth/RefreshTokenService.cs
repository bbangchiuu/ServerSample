using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ServerSampleApi.Data;
using ServerSampleApi.Models;

namespace ServerSampleApi.Auth
{
    /// <summary>Refresh token vừa phát (token GỐC, chỉ gửi cho client đúng một lần) và thời điểm hết hạn.</summary>
    public record IssuedRefreshToken(string Token, DateTime ExpiresAt);

    /// <summary>Kết quả làm mới thành công: token cũ đã bị thu hồi, đây là token mới thay thế.</summary>
    public record RotatedRefreshToken(Guid AccountId, IssuedRefreshToken NewToken);

    /// <summary>
    /// Phát, làm mới và thu hồi refresh token.
    ///
    /// Refresh token là một chuỗi NGẪU NHIÊN (không phải JWT, không chứa thông tin gì). Server lưu bản băm trong database,
    /// nên muốn biết token thuộc về ai, còn hạn không, đã bị thu hồi chưa thì phải tra database.
    /// Chậm hơn kiểm tra JWT, nhưng chỉ tra khi làm mới (15 phút một lần), không phải mỗi request.
    ///
    /// Xoay vòng (rotation): mỗi refresh token chỉ dùng được MỘT lần. Dùng xong là bị thu hồi và đổi lấy token mới.
    /// Scoped vì dùng GameDbContext.
    /// </summary>
    public class RefreshTokenService
    {
        private readonly GameDbContext _db;
        private readonly JwtOptions _options;

        public RefreshTokenService(GameDbContext db, IOptions<JwtOptions> options)
        {
            _db = db;
            _options = options.Value;
        }

        /// <summary>Phát refresh token mới cho tài khoản (lúc đăng ký, đăng nhập) và lưu ngay vào database.</summary>
        public async Task<IssuedRefreshToken> IssueAsync(Guid accountId)
        {
            IssuedRefreshToken issued = AddNewToken(accountId);
            await _db.SaveChangesAsync();
            return issued;
        }

        /// <summary>
        /// Đổi refresh token cũ lấy token mới. Trả về null nếu token không hợp lệ: không có, hết hạn, hoặc đã bị thu hồi.
        /// </summary>
        public async Task<RotatedRefreshToken?> RotateAsync(string token)
        {
            DateTime now = DateTime.UtcNow;

            // Băm TRƯỚC rồi mới đưa vào câu truy vấn: EF Core dịch lambda bên dưới sang SQL,
            // mà PostgreSQL không biết hàm Hash() của C# là gì.
            string tokenHash = Hash(token);
            RefreshTokenModel? stored = await _db.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

            if (stored == null || stored.ExpiresAt <= now)
            {
                return null;
            }

            if (stored.RevokedAt != null)
            {
                if (stored.ReplacedByTokenId != null)
                {
                    await RevokeAllBecauseOfReuseAsync(stored.AccountId);
                }
                // Còn token đã đăng xuất mà bị gửi lại thì chỉ từ chối, không đụng tới các thiết bị khác.
                return null;
            }

            // Thu hồi token cũ và ghi lại token mới thay thế nó. Chỉ thành công nếu token cũ CHƯA bị thu hồi:
            // SQL: UPDATE "RefreshTokens" SET "RevokedAt" = now, "ReplacedByTokenId" = @newId
            //      WHERE "Id" = @id AND "RevokedAt" IS NULL
            // Viết trong MỘT câu lệnh (không đọc rồi mới sửa) để 2 request dùng cùng một token cùng lúc
            // thì chỉ 1 request đổi được, request kia thấy 0 dòng bị sửa.
            Guid newTokenId = Guid.NewGuid();
            int revoked = await _db.RefreshTokens
                .Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.RevokedAt, now)
                    .SetProperty(t => t.ReplacedByTokenId, newTokenId));

            if (revoked == 0)
            {
                // Có request khác vừa dùng đúng token này, ngay giữa lúc mình đọc và lúc mình sửa.
                await RevokeAllBecauseOfReuseAsync(stored.AccountId);
                return null;
            }

            IssuedRefreshToken newToken = AddNewToken(stored.AccountId, newTokenId);
            await _db.SaveChangesAsync();
            return new RotatedRefreshToken(stored.AccountId, newToken);
        }

        /// <summary>Thu hồi một refresh token (đăng xuất). Token không có hoặc đã bị thu hồi thì không làm gì.</summary>
        public async Task RevokeAsync(string token)
        {
            string tokenHash = Hash(token);
            await _db.RefreshTokens
                .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        }

        /// <summary>Thu hồi mọi refresh token còn dùng được của một tài khoản (đăng xuất khỏi mọi thiết bị).</summary>
        public async Task RevokeAllAsync(Guid accountId)
        {
            await _db.RefreshTokens
                .Where(t => t.AccountId == accountId && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        }

        /// <summary>
        /// Xoá hẳn các refresh token đã hết hạn. Trả về số dòng đã xoá. Được RefreshTokenCleanupService gọi định kỳ.
        /// SQL: DELETE FROM "RefreshTokens" WHERE "ExpiresAt" &lt; now
        ///
        /// Chỉ xoá theo ExpiresAt, KHÔNG xoá token đã thu hồi mà còn hạn: những dòng đó vẫn cần để phát hiện token bị
        /// đánh cắp (token đã dùng bị gửi lại). Còn token hết hạn thì RotateAsync từ chối ngay ở bước kiểm tra hạn,
        /// trước cả bước xét dùng lại, nên dòng đó không còn tác dụng gì.
        /// </summary>
        public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
        {
            DateTime now = DateTime.UtcNow;
            return await _db.RefreshTokens
                .Where(t => t.ExpiresAt < now)
                .ExecuteDeleteAsync(cancellationToken);
        }

        /// <summary>
        /// Một token ĐÃ DÙNG để làm mới mà vẫn có người gửi lại: dấu hiệu token bị đánh cắp. Kẻ xấu và người chơi thật
        /// cùng giữ một token, ai dùng sau sẽ rơi vào đây. Không biết ai là thật, nên thu hồi TẤT CẢ token của tài khoản:
        /// cả hai bên đều phải đăng nhập lại bằng mật khẩu, và chỉ người chơi thật biết mật khẩu.
        /// </summary>
        private Task RevokeAllBecauseOfReuseAsync(Guid accountId) => RevokeAllAsync(accountId);

        /// <summary>Tạo token ngẫu nhiên, đánh dấu "sẽ thêm" vào database (chưa lưu), trả về token GỐC cho client.</summary>
        private IssuedRefreshToken AddNewToken(Guid accountId, Guid? id = null)
        {
            // 32 byte ngẫu nhiên = 256 bit: không thể đoán được. RandomNumberGenerator là bộ sinh số ngẫu nhiên
            // dùng cho bảo mật, khác Random (Random đoán được nếu biết vài số trước đó).
            // Base64Url: chỉ gồm chữ, số, "-" và "_", gửi trong JSON hay URL đều không phải mã hoá thêm.
            string token = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            DateTime now = DateTime.UtcNow;
            DateTime expiresAt = now.AddDays(_options.RefreshTokenDays);

            _db.RefreshTokens.Add(new RefreshTokenModel
            {
                Id = id ?? Guid.NewGuid(),
                AccountId = accountId,
                TokenHash = Hash(token),
                CreatedAt = now,
                ExpiresAt = expiresAt,
            });

            return new IssuedRefreshToken(token, expiresAt);
        }

        /// <summary>
        /// SHA-256 dạng hex (64 ký tự). Mật khẩu thì phải băm CHẬM (PBKDF2) vì người ta hay đặt mật khẩu dễ đoán,
        /// kẻ xấu thử hàng tỷ mật khẩu phổ biến được. Token thì ngẫu nhiên 256 bit, không có gì để đoán, nên băm nhanh là đủ.
        /// </summary>
        public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
