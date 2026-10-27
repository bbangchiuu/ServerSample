using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API dữ liệu người chơi.
    ///
    /// ⚠ CHƯA ĐƯỢC BẢO VỆ: ai biết playerId là xem được dữ liệu của người đó.
    /// Giai đoạn 4 (JWT) sẽ sửa: server lấy playerId từ token đăng nhập, không tin playerId client gửi lên.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")] // → /api/Players
    public class PlayersController : ControllerBase
    {
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
            Player? player = _playerStore.Find(playerId);
            if (player == null)
            {
                // 404 Not Found: không có người chơi này. [ApiController] tự thêm body dạng ProblemDetails.
                return NotFound();
            }

            return Ok(new PlayerProfileResponse
            {
                Id = player.Id,
                DisplayName = player.DisplayName,
                Level = player.Level,
                Gold = player.Gold,
            });
        }
    }
}
