using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")] // [controller] = tên class bỏ chữ "Controller" → /api/Accounts
    public class AccountsController : ControllerBase
    {
        private readonly AccountStore _accountStore;
        private readonly PlayerStore _playerStore;
        private readonly IPasswordHasher<Account> _passwordHasher;

        // ASP.NET Core tự tạo Controller cho mỗi request và tự truyền các service vào đây
        // (Dependency Injection), dựa trên những gì đã đăng ký trong Program.cs.
        public AccountsController(AccountStore accountStore, PlayerStore playerStore, IPasswordHasher<Account> passwordHasher)
        {
            _accountStore = accountStore;
            _playerStore = playerStore;
            _passwordHasher = passwordHasher;
        }

        // POST /api/accounts/register
        [HttpPost("register")]
        [ProducesResponseType(typeof(AccountResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<AccountResponse> Register(RegisterRequest request)
        {
            // Chạy được tới đây nghĩa là request đã hợp lệ: [ApiController] đã tự kiểm tra
            // [Required], [StringLength]... trong RegisterRequest, sai thì tự trả 400 mà không gọi hàm này.

            var account = new Account
            {
                Id = Guid.NewGuid(),
                Username = request.Username,
                CreatedAt = DateTime.UtcNow,
            };
            account.PasswordHash = _passwordHasher.HashPassword(account, request.Password);

            // Tạo nhân vật TRƯỚC khi thêm tài khoản: tài khoản vừa xuất hiện (đăng nhập được) là
            // nhân vật đã có sẵn, không có khoảnh khắc nào đăng nhập được mà xem profile lại bị 404.
            _playerStore.Add(new Player
            {
                Id = account.Id,
                DisplayName = account.Username,
            });

            if (!_accountStore.TryAdd(account))
            {
                // Trùng tên: bỏ nhân vật vừa tạo.
                _playerStore.Remove(account.Id);

                // 409 Conflict: yêu cầu hợp lệ, nhưng xung đột với dữ liệu đang có (tên đã có người dùng).
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
        public ActionResult<AccountResponse> Login(LoginRequest request)
        {
            Account? account = _accountStore.FindByUsername(request.Username);

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
