using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;
using ServerSampleApi.Models;
using ServerSampleApi.Services;
using StackExchange.Redis;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API dữ liệu của CHÍNH người chơi đang đăng nhập.
    ///
    /// [Authorize]: mọi API trong controller này đều đòi token hợp lệ. Không có token, token sai chữ ký
    /// hoặc hết hạn thì ASP.NET Core trả 401 Unauthorized ngay, KHÔNG chạy vào hàm nào bên dưới.
    ///
    /// URL không còn {playerId}: "me" nghĩa là "người đang đăng nhập", và server lấy playerId từ token
    /// (User.GetPlayerId()). Client không còn cách nào gửi playerId của người khác lên nữa.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/players/me")]
    public class PlayersController : ControllerBase
    {
        /// <summary>Phần thưởng mỗi ngày. Do SERVER quyết định, client không gửi lên được.</summary>
        public const int DailyRewardGold = 100;
        public const string DailyRewardItemId = "potion";

        private readonly GameDbContext _db;
        private readonly LeaderboardService _leaderboard;
        private readonly IHubContext<GameHub, IGameClient> _hub;
        private readonly ILogger<PlayersController> _logger;

        // IHubContext: "cửa" để code BÊN NGOÀI hub (controller, service, BackgroundService...) gửi tin xuống client.
        // Có Clients.All, Clients.User(...) giống bên trong hub, nhưng không có Context (không có "người đang gọi"
        // của hub, vì người gọi ở đây là một request HTTP).
        public PlayersController(
            GameDbContext db,
            LeaderboardService leaderboard,
            IHubContext<GameHub, IGameClient> hub,
            ILogger<PlayersController> logger)
        {
            _db = db;
            _leaderboard = leaderboard;
            _hub = hub;
            _logger = logger;
        }

        // GET /api/players/me
        [HttpGet]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PlayerProfileResponse>> GetMyProfile()
        {
            Guid playerId = User.GetPlayerId();

            // FindAsync: tìm theo khoá chính. SQL: SELECT ... FROM "Players" WHERE "Id" = @p
            PlayerModel? player = await _db.Players.FindAsync(playerId);
            if (player == null)
            {
                // Token hợp lệ nhưng người chơi không còn (ví dụ tài khoản đã bị xoá sau khi phát token).
                return NotFound();
            }

            return Ok(ToProfileResponse(player));
        }

        // PUT /api/players/me/display-name
        // PUT = "đặt giá trị này": gửi cùng một request 2 lần thì kết quả vẫn y như gửi 1 lần.
        [HttpPut("display-name")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PlayerProfileResponse>> ChangeDisplayName(ChangeDisplayNameRequest request)
        {
            Guid playerId = User.GetPlayerId();

            // Bỏ khoảng trắng thừa ở hai đầu. Phải kiểm tra lại độ dài SAU khi bỏ:
            // "   ab   " vượt qua [StringLength(MinimumLength = 3)] nhưng thực ra chỉ có 2 ký tự.
            string displayName = request.DisplayName.Trim();
            if (displayName.Length < 3)
            {
                // Tự thêm lỗi theo đúng định dạng của [ApiController] → client nhận cùng một kiểu lỗi 400.
                ModelState.AddModelError(nameof(request.DisplayName), "Display name must have at least 3 non-space characters.");
                return ValidationProblem(ModelState);
            }

            PlayerModel? player = await _db.Players.FindAsync(playerId);
            if (player == null)
            {
                return NotFound();
            }

            // EF Core "theo dõi" object vừa đọc ra: sửa property xong, SaveChangesAsync tự sinh
            // câu SQL UPDATE "Players" SET "DisplayName" = @p WHERE "Id" = @id, chỉ cho đúng cột đã đổi.
            player.DisplayName = displayName;
            await _db.SaveChangesAsync();

            // Trả về profile sau khi sửa, để client cập nhật giao diện mà không cần gọi GET thêm lần nữa.
            // Đồng thời đẩy profile mới xuống các thiết bị KHÁC của người này đang mở game.
            PlayerProfileResponse profile = ToProfileResponse(player);
            await NotifyProfileChangedAsync(profile);
            return Ok(profile);
        }

        // POST /api/players/me/daily-reward
        // POST vì đây là một HÀNH ĐỘNG: gửi 2 lần thì lần 2 bị từ chối, kết quả khác lần 1 (không idempotent).
        [HttpPost("daily-reward")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<PlayerProfileResponse>> ClaimDailyReward()
        {
            Guid playerId = User.GetPlayerId();

            // Dùng giờ UTC để "một ngày" là như nhau với mọi người chơi, ở bất kỳ múi giờ nào.
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

            // Transaction: hai câu lệnh bên dưới (cộng vàng + thêm potion) cùng thành công, hoặc cùng bị huỷ.
            // "await using": ra khỏi hàm mà chưa Commit thì transaction tự bị huỷ (rollback).
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // ExecuteUpdateAsync gửi thẳng MỘT câu lệnh UPDATE, không đọc người chơi ra C# trước:
            //   UPDATE "Players"
            //   SET "Gold" = "Gold" + 100, "LastDailyRewardDate" = @today
            //   WHERE "Id" = @playerId AND ("LastDailyRewardDate" IS NULL OR "LastDailyRewardDate" < @today)
            //
            // Vì sao an toàn khi 20 request đến cùng lúc:
            //  - Điều kiện "hôm nay chưa nhận" được kiểm tra NGAY TRONG câu UPDATE, không còn khe hở
            //    giữa "kiểm tra" và "ghi" như khi đọc ra C# rồi mới quyết định.
            //  - PostgreSQL khoá dòng của người chơi trong lúc UPDATE: request thứ hai phải chờ request đầu
            //    xong, rồi kiểm tra lại điều kiện, lúc đó thấy ngày đã là hôm nay nên không cập nhật dòng nào.
            //  - "Gold" + 100 được tính BÊN TRONG database, nên không còn lost update.
            int updatedRows = await _db.Players
                .Where(p => p.Id == playerId
                    && (p.LastDailyRewardDate == null || p.LastDailyRewardDate < today))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(p => p.Gold, p => p.Gold + DailyRewardGold)
                    .SetProperty(p => p.LastDailyRewardDate, today));

            if (updatedRows == 0)
            {
                // Không dòng nào được cập nhật: hoặc người chơi không tồn tại, hoặc hôm nay đã nhận rồi.
                if (!await _db.Players.AnyAsync(p => p.Id == playerId))
                {
                    return NotFound();
                }

                return Problem(
                    title: "Daily reward already claimed",
                    detail: "Come back tomorrow (UTC).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Thêm 1 potion: chưa có thì tạo dòng mới, có rồi thì cộng thêm 1. Cũng là MỘT câu lệnh ("upsert").
            // EF Core chưa có hàm sẵn cho việc này nên viết SQL trực tiếp. ExecuteSqlAsync với chuỗi $"..."
            // tự biến {playerId}, {DailyRewardItemId} thành THAM SỐ an toàn, không ghép chuỗi
            // (ghép chuỗi SQL bằng tay là lỗ hổng SQL injection).
            await _db.Database.ExecuteSqlAsync($"""
                INSERT INTO "InventoryItems" ("PlayerId", "ItemId", "Quantity")
                VALUES ({playerId}, {DailyRewardItemId}, 1)
                ON CONFLICT ("PlayerId", "ItemId")
                DO UPDATE SET "Quantity" = "InventoryItems"."Quantity" + 1
                """);

            await transaction.CommitAsync();

            // Đọc lại profile sau khi cập nhật để trả về cho client.
            PlayerModel player = await _db.Players.AsNoTracking().FirstAsync(p => p.Id == playerId);

            // Cập nhật bảng xếp hạng SAU khi PostgreSQL đã commit, bằng số vàng vừa đọc từ PostgreSQL.
            LeaderboardEntry? entry = await UpdateLeaderboardAsync(player);

            // Mọi thứ đã lưu xong mới báo cho client: báo trước khi commit mà commit thất bại thì client
            // sẽ thấy số vàng không có thật.
            PlayerProfileResponse profile = ToProfileResponse(player);
            await NotifyProfileChangedAsync(profile);
            if (entry != null)
            {
                // Clients.All: mọi người đang online, kể cả chính người vừa nhận thưởng.
                await _hub.Clients.All.LeaderboardChanged(new LeaderboardEntryResponse
                {
                    Rank = entry.Rank,
                    PlayerId = player.Id,
                    DisplayName = player.DisplayName,
                    Gold = entry.Gold,
                });
            }

            return Ok(profile);
        }

        /// <summary>
        /// Ghi số vàng của người chơi lên bảng xếp hạng Redis, rồi đọc lại hạng mới (để báo cho mọi người).
        /// Trả về null nếu Redis lỗi.
        ///
        /// Redis lỗi (chưa bật, rớt mạng...) thì CHỈ ghi log cảnh báo, không trả lỗi cho client:
        /// vàng và potion đã lưu chắc chắn trong PostgreSQL rồi. Nếu trả 500 ở đây, client tưởng nhận thưởng
        /// thất bại và bấm lại, rồi bị báo "hôm nay đã nhận", rất khó hiểu. Bảng xếp hạng chậm cập nhật
        /// một chút vẫn hơn làm hỏng chức năng chính.
        /// </summary>
        private async Task<LeaderboardEntry?> UpdateLeaderboardAsync(PlayerModel player)
        {
            try
            {
                await _leaderboard.SetGoldAsync(player.Id, player.Gold);
                return await _leaderboard.GetPlayerAsync(player.Id);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                _logger.LogWarning(ex, "Could not update leaderboard for player {PlayerId}", player.Id);
                return null;
            }
        }

        /// <summary>
        /// Đẩy profile mới xuống MỌI kết nối của người chơi này (Clients.User, xem PlayerIdUserIdProvider).
        /// Người đó không mở kết nối nào thì không có ai nhận, cũng không lỗi gì.
        /// </summary>
        private Task NotifyProfileChangedAsync(PlayerProfileResponse profile)
        {
            return _hub.Clients.User(profile.Id.ToString()).ProfileChanged(profile);
        }

        /// <summary>Chuyển Player (dữ liệu bên trong) thành DTO trả về.</summary>
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
