using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API dữ liệu người chơi.
    ///
    /// ⚠ CHƯA ĐƯỢC BẢO VỆ: ai biết playerId là xem và sửa được dữ liệu của người đó.
    /// Giai đoạn 4 (JWT) sẽ sửa: server lấy playerId từ token đăng nhập, không tin playerId client gửi lên.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")] // → /api/Players
    public class PlayersController : ControllerBase
    {
        /// <summary>Số vàng nhận được mỗi ngày. Do SERVER quyết định, client không gửi lên được.</summary>
        public const int DailyRewardGold = 100;

        private readonly PlayerStore _playerStore;

        public PlayersController(PlayerStore playerStore)
        {
            _playerStore = playerStore;
        }

        // GET /api/players/{playerId}
        // {playerId:guid} là "route constraint": chỉ khớp khi phần này là một Guid hợp lệ,
        // ví dụ /api/players/abc sẽ không khớp và trả 404. ASP.NET Core tự chuyển chuỗi thành Guid.
        [HttpGet("{playerId:guid}")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<PlayerProfileResponse> GetProfile(Guid playerId)
        {
            PlayerModel? player = _playerStore.Find(playerId);
            if (player == null)
            {
                // 404 Not Found: không có người chơi này. [ApiController] tự thêm body dạng ProblemDetails.
                return NotFound();
            }

            // Đọc cũng phải lock: để không đọc phải lúc một request khác đang sửa dở người chơi này.
            lock (player.SyncRoot)
            {
                return Ok(ToProfileResponse(player));
            }
        }

        // PUT /api/players/{playerId}/display-name
        // PUT = "đặt giá trị này": gửi cùng một request 2 lần thì kết quả vẫn y như gửi 1 lần.
        [HttpPut("{playerId:guid}/display-name")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<PlayerProfileResponse> ChangeDisplayName(Guid playerId, ChangeDisplayNameRequest request)
        {
            // playerId lấy từ URL, request lấy từ JSON body: ASP.NET Core tự phân biệt
            // (tham số có tên trùng {playerId} trong route → lấy từ route; class phức tạp → lấy từ body).

            // Bỏ khoảng trắng thừa ở hai đầu. Phải kiểm tra lại độ dài SAU khi bỏ:
            // "   ab   " vượt qua [StringLength(MinimumLength = 3)] nhưng thực ra chỉ có 2 ký tự.
            string displayName = request.DisplayName.Trim();
            if (displayName.Length < 3)
            {
                // Tự thêm lỗi theo đúng định dạng của [ApiController] → client nhận cùng một kiểu lỗi 400.
                ModelState.AddModelError(nameof(request.DisplayName), "Display name must have at least 3 non-space characters.");
                return ValidationProblem(ModelState);
            }

            PlayerModel? player = _playerStore.Find(playerId);
            if (player == null)
            {
                return NotFound();
            }

            lock (player.SyncRoot)
            {
                player.DisplayName = displayName;

                // Trả về profile sau khi sửa, để client cập nhật giao diện mà không cần gọi GET thêm lần nữa.
                return Ok(ToProfileResponse(player));
            }
        }

        // POST /api/players/{playerId}/daily-reward
        // POST vì đây là một HÀNH ĐỘNG: gửi 2 lần thì lần 2 bị từ chối, kết quả khác lần 1 (không idempotent).
        [HttpPost("{playerId:guid}/daily-reward")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<PlayerProfileResponse> ClaimDailyReward(Guid playerId)
        {
            PlayerModel? player = _playerStore.Find(playerId);
            if (player == null)
            {
                return NotFound();
            }

            // Dùng giờ UTC để "một ngày" là như nhau với mọi người chơi, ở bất kỳ múi giờ nào.
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

            // KIỂM TRA và CỘNG THƯỞNG phải nằm trong CÙNG một lock.
            // Không có lock: 2 request gửi cùng lúc (bấm 2 lần, hoặc cố tình gian lận) đều kiểm tra thấy
            // "hôm nay chưa nhận", rồi đều cộng thưởng → người chơi nhận được nhiều lần.
            lock (player.SyncRoot)
            {
                if (player.LastDailyRewardDate == today)
                {
                    return Problem(
                        title: "Daily reward already claimed",
                        detail: "Come back tomorrow (UTC).",
                        statusCode: StatusCodes.Status409Conflict);
                }

                player.Gold += DailyRewardGold;
                player.LastDailyRewardDate = today;

                return Ok(ToProfileResponse(player));
            }
        }

        /// <summary>Chuyển Player (dữ liệu bên trong) thành DTO trả về. Gọi bên trong lock.</summary>
        private static PlayerProfileResponse ToProfileResponse(PlayerModel player)
        {
            return new PlayerProfileResponse
            {
                Id = player.Id,
                DisplayName = player.DisplayName,
                Level = player.Level,
                Gold = player.Gold,
                LastDailyRewardDate = player.LastDailyRewardDate,
            };
        }
    }
}
