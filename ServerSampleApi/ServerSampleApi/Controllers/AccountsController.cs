using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;

namespace ServerSampleApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // [controller] = tên class bỏ chữ "Controller" → /api/Accounts
    public class AccountsController : ControllerBase
    {
        private readonly GameDbContext _db;
        private readonly IPasswordHasher<AccountModel> _passwordHasher;
        private readonly TokenService _tokenService;
        private readonly RefreshTokenService _refreshTokens;

        // ASP.NET Core tự tạo Controller cho mỗi request và tự truyền các service vào đây
        // (Dependency Injection), dựa trên những gì đã đăng ký trong Program.cs.
        // GameDbContext là Scoped: mỗi request nhận một GameDbContext riêng.
        public AccountsController(
            GameDbContext db,
            IPasswordHasher<AccountModel> passwordHasher,
            TokenService tokenService,
            RefreshTokenService refreshTokens)
        {
            _db = db;
            _passwordHasher = passwordHasher;
            _tokenService = tokenService;
            _refreshTokens = refreshTokens;
        }

        // POST /api/accounts/register
        // async Task<...>: đọc/ghi database là chờ mạng, nên dùng await để không giữ thread trong lúc chờ.
        [HttpPost("register")]
        [EnableRateLimiting(RateLimitingOptions.RegisterPolicy)] // mỗi IP tối đa 10 tài khoản mỗi giờ (xem appsettings.json)
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<AccountResponse>> Register(RegisterRequest request)
        {
            // Chạy được tới đây nghĩa là request đã hợp lệ: [ApiController] đã tự kiểm tra
            // [Required], [StringLength]... trong RegisterRequest, sai thì tự trả 400 mà không gọi hàm này.

            var account = new AccountModel
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                CreatedAt = DateTime.UtcNow,
            };
            account.PasswordHash = _passwordHasher.HashPassword(account, request.Password);

            // Add chỉ đánh dấu "sẽ thêm", CHƯA ghi gì vào database.
            _db.Accounts.Add(account);
            _db.Players.Add(new PlayerModel
            {
                Id = account.Id,
                DisplayName = account.Username,
            });

            try
            {
                // SaveChangesAsync ghi CẢ HAI dòng trong MỘT transaction: hoặc cả hai cùng được lưu,
                // hoặc không dòng nào được lưu. Không bao giờ có tài khoản mà thiếu nhân vật.
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Unique index trên Username từ chối: tên đã có người dùng (không phân biệt hoa thường).
                // Database là "trọng tài" duy nhất: 2 request đăng ký cùng tên cùng lúc thì chỉ 1 cái thành công.
                // 409 Conflict: yêu cầu hợp lệ, nhưng xung đột với dữ liệu đang có.
                return Problem(
                    title: "Username already taken",
                    detail: $"The username '{request.Username}' is already in use.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Đăng ký xong là đăng nhập luôn: trả token ngay, client không phải gọi login thêm lần nữa.
            IssuedRefreshToken refreshToken = await _refreshTokens.IssueAsync(account.Id);
            AccountResponse response = ToAccountResponse(account, refreshToken);

            // 201 Created: đã tạo tài nguyên mới. Header "Location" chỉ tới profile người chơi (/api/players/me).
            // CreatedAtAction tự sinh đường dẫn từ route thật của PlayersController.GetMyProfile,
            // nên sau này đổi route bên đó thì Location ở đây cũng tự đúng theo.
            return CreatedAtAction(
                nameof(PlayersController.GetMyProfile),
                "Players",
                routeValues: null,
                response);
        }

        // POST /api/accounts/login
        // Đúng mật khẩu thì trả về JWT. Bước tiếp theo các API khác sẽ đòi token này.
        [HttpPost("login")]
        [EnableRateLimiting(RateLimitingOptions.LoginPolicy)] // mỗi IP tối đa 5 lần thử mỗi phút: chống dò mật khẩu
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public async Task<ActionResult<AccountResponse>> Login(LoginRequest request)
        {
            // EF Core dịch dòng này thành câu SQL: SELECT ... FROM "Accounts" WHERE "Username" = @p LIMIT 1
            // Cột Username kiểu citext nên "Bang" vẫn tìm ra tài khoản "bang".
            // AsNoTracking: chỉ đọc, không sửa, nên bảo EF khỏi theo dõi thay đổi (nhanh hơn).
            AccountModel? account = await _db.Accounts
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Username == request.Username);

            // Băm mật khẩu vừa gửi lên rồi so với bản băm đã lưu. Không bao giờ "giải băm" được,
            // nên cách duy nhất để kiểm tra là băm lại và so sánh.
            bool passwordCorrect = account != null
                && _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, request.Password)
                    != PasswordVerificationResult.Failed;

            if (!passwordCorrect)
            {
                // Sai tên hay sai mật khẩu đều trả CÙNG một lỗi. Nếu báo riêng "tên không tồn tại",
                // kẻ xấu sẽ dò ra được danh sách tài khoản có thật để tấn công tiếp.
                return Problem(
                    title: "Invalid username or password",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            // 200 OK. account! : tới đây chắc chắn account khác null (đã kiểm tra trong passwordCorrect).
            // Mỗi lần đăng nhập là một refresh token MỚI: đăng nhập trên 2 máy thì 2 máy có 2 token riêng,
            // đăng xuất máy này không làm máy kia bị đăng xuất.
            IssuedRefreshToken refreshToken = await _refreshTokens.IssueAsync(account!.Id);
            return Ok(ToAccountResponse(account, refreshToken));
        }

        // POST /api/accounts/refresh
        // Access token hết hạn: client gửi refresh token lên để nhận CẶP token mới (access token + refresh token),
        // không cần nhập lại mật khẩu. Refresh token cũ bị thu hồi, client phải lưu refresh token mới thay cho nó.
        [HttpPost("refresh")]
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<AccountResponse>> Refresh(RefreshTokenRequest request)
        {
            RotatedRefreshToken? rotated = await _refreshTokens.RotateAsync(request.RefreshToken);

            // Đọc lại tài khoản từ database: lấy Username mới nhất cho token, và tài khoản đã bị xoá thì không cấp token nữa.
            AccountModel? account = rotated == null
                ? null
                : await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == rotated.AccountId);

            if (rotated == null || account == null)
            {
                // Không nói rõ lý do (không có, hết hạn hay đã bị thu hồi): client xử lý như nhau, là quay về màn hình đăng nhập.
                return Problem(
                    title: "Invalid refresh token",
                    detail: "Please log in again.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            return Ok(ToAccountResponse(account, rotated.NewToken));
        }

        // POST /api/accounts/logout
        // Thu hồi refresh token: từ giờ không làm mới được nữa. Access token đang có vẫn dùng được tới lúc hết hạn
        // (tối đa AccessTokenMinutes), vì server không lưu JWT nên không thu hồi được. Đó là lý do access token phải ngắn.
        // Luôn trả 204, kể cả khi token không có thật: kết quả mà client muốn ("token này không dùng được nữa") đã đúng rồi.
        [HttpPost("logout")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Logout(RefreshTokenRequest request)
        {
            await _refreshTokens.RevokeAsync(request.RefreshToken);
            return NoContent();
        }

        /// <summary>Tạo access token mới cho tài khoản, rồi gói cùng refresh token vào DTO trả về.</summary>
        private AccountResponse ToAccountResponse(AccountModel account, IssuedRefreshToken refreshToken)
        {
            AccessToken accessToken = _tokenService.CreateAccessToken(account);
            return new AccountResponse
            {
                PlayerId = account.Id,
                Username = account.Username,
                AccessToken = accessToken.Token,
                AccessTokenExpiresAt = accessToken.ExpiresAt,
                RefreshToken = refreshToken.Token,
                RefreshTokenExpiresAt = refreshToken.ExpiresAt,
            };
        }
    }
}
