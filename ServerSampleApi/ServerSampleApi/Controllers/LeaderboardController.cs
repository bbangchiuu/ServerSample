using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API bảng xếp hạng vàng.
    /// Thứ hạng và số vàng đọc từ Redis (nhanh), tên hiển thị đọc từ PostgreSQL (vì tên đổi được).
    /// </summary>
    [ApiController]
    [Route("api/[controller]")] // → /api/Leaderboard
    public class LeaderboardController : ControllerBase
    {
        private readonly LeaderboardService _leaderboard;
        private readonly GameDbContext _db;

        public LeaderboardController(LeaderboardService leaderboard, GameDbContext db)
        {
            _leaderboard = leaderboard;
            _db = db;
        }

        // GET /api/leaderboard?count=10
        // [FromQuery]: lấy từ phần "?count=10" trên URL. Không gửi thì mặc định 10.
        // [Range(1, 100)]: [ApiController] tự trả 400 nếu count nằm ngoài khoảng này,
        // để không ai xin cả triệu dòng một lúc.
        [HttpGet]
        [ProducesResponseType(typeof(LeaderboardResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<LeaderboardResponse>> GetTop([FromQuery, Range(1, 100)] int count = 10)
        {
            List<LeaderboardEntry> top = await _leaderboard.GetTopAsync(count);

            // Lấy tên của cả top bằng MỘT câu SQL: SELECT "Id", "DisplayName" FROM "Players" WHERE "Id" IN (...)
            // Không gọi FindAsync trong vòng lặp, vì như vậy là 10 người thì 10 câu SQL (lỗi "N+1 query").
            List<Guid> playerIds = top.Select(entry => entry.PlayerId).ToList();
            Dictionary<Guid, string> displayNames = await _db.Players
                .Where(p => playerIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.DisplayName);

            return Ok(new LeaderboardResponse
            {
                Entries = top.Select(entry => ToEntryResponse(entry, displayNames.GetValueOrDefault(entry.PlayerId, ""))).ToList(),
            });
        }

        // GET /api/leaderboard/players/{playerId}
        // Hạng của MỘT người chơi, kể cả khi người đó không nằm trong top.
        [HttpGet("players/{playerId:guid}")]
        [ProducesResponseType(typeof(LeaderboardEntryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<LeaderboardEntryResponse>> GetPlayerRank(Guid playerId)
        {
            LeaderboardEntry? entry = await _leaderboard.GetPlayerAsync(playerId);
            if (entry == null)
            {
                // Không có người chơi này, hoặc người đó chưa từng được cộng vàng nên chưa có trên bảng.
                return NotFound();
            }

            string displayName = await _db.Players
                .Where(p => p.Id == playerId)
                .Select(p => p.DisplayName)
                .FirstOrDefaultAsync() ?? "";

            return Ok(ToEntryResponse(entry, displayName));
        }

        // POST /api/leaderboard/rebuild
        // Dựng lại toàn bộ bảng xếp hạng từ PostgreSQL. Dùng khi Redis bị thiếu hoặc sai dữ liệu.
        // ⚠ CHƯA ĐƯỢC BẢO VỆ: ai cũng gọi được. Giai đoạn 4 (JWT) sẽ giới hạn chỉ admin mới gọi được.
        [HttpPost("rebuild")]
        [ProducesResponseType(typeof(LeaderboardRebuildResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<LeaderboardRebuildResponse>> Rebuild()
        {
            int playerCount = await _leaderboard.RebuildAsync();
            return Ok(new LeaderboardRebuildResponse { PlayerCount = playerCount });
        }

        private static LeaderboardEntryResponse ToEntryResponse(LeaderboardEntry entry, string displayName)
        {
            return new LeaderboardEntryResponse
            {
                Rank = entry.Rank,
                PlayerId = entry.PlayerId,
                DisplayName = displayName,
                Gold = entry.Gold,
            };
        }
    }
}
