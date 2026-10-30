using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        // ASP.NET Core tự tạo Controller cho mỗi request và tự truyền các service vào đây
        // (Dependency Injection), dựa trên những gì đã đăng ký trong Program.cs.
        // GameDbContext là Scoped: mỗi request nhận một GameDbContext riêng.
        public AccountsController(GameDbContext db, IPasswordHasher<AccountModel> passwordHasher)
        {
            _db = db;
            _passwordHasher = passwordHasher;
        }

        // POST /api/accounts/register
        // async Task<...>: đọc/ghi database là chờ mạng, nên dùng await để không giữ thread trong lúc chờ.
        [HttpPost("register")]
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
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

            var response = new AccountResponse
            {
                PlayerId = account.Id,
                Username = account.Username,
            };

            // 201 Created: đã tạo tài nguyên mới. Header "Location" chỉ tới profile người chơi.
            // CreatedAtAction tự sinh đường dẫn từ route thật của PlayersController.GetProfile,
            // nên sau này đổi route bên đó thì Location ở đây cũng tự đúng theo.
            return CreatedAtAction(
                nameof(PlayersController.GetProfile),
                "Players",
                new { playerId = account.Id },
                response);
        }

        // POST /api/accounts/login
        // Giai đoạn này đăng nhập chỉ trả về PlayerId. Giai đoạn 4 sẽ trả về JWT token để bảo vệ các API khác.
        [HttpPost("login")]
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
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
            return Ok(new AccountResponse
            {
                PlayerId = account!.Id,
                Username = account.Username,
            });
        }
    }
}
