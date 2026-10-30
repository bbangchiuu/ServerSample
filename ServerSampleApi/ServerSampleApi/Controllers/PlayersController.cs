using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;

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
        /// <summary>Phần thưởng mỗi ngày. Do SERVER quyết định, client không gửi lên được.</summary>
        public const int DailyRewardGold = 100;
        public const string DailyRewardItemId = "potion";

        private readonly GameDbContext _db;

        public PlayersController(GameDbContext db)
        {
            _db = db;
        }

        // GET /api/players/{playerId}
        // {playerId:guid} là "route constraint": chỉ khớp khi phần này là một Guid hợp lệ,
        // ví dụ /api/players/abc sẽ không khớp và trả 404. ASP.NET Core tự chuyển chuỗi thành Guid.
        [HttpGet("{playerId:guid}")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PlayerProfileResponse>> GetProfile(Guid playerId)
        {
            // FindAsync: tìm theo khoá chính. SQL: SELECT ... FROM "Players" WHERE "Id" = @p
            PlayerModel? player = await _db.Players.FindAsync(playerId);
            if (player == null)
            {
                // 404 Not Found: không có người chơi này. [ApiController] tự thêm body dạng ProblemDetails.
                return NotFound();
            }

            return Ok(ToProfileResponse(player));
        }

        // PUT /api/players/{playerId}/display-name
        // PUT = "đặt giá trị này": gửi cùng một request 2 lần thì kết quả vẫn y như gửi 1 lần.
        [HttpPut("{playerId:guid}/display-name")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PlayerProfileResponse>> ChangeDisplayName(Guid playerId, ChangeDisplayNameRequest request)
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
            return Ok(ToProfileResponse(player));
        }

        // POST /api/players/{playerId}/daily-reward
        // POST vì đây là một HÀNH ĐỘNG: gửi 2 lần thì lần 2 bị từ chối, kết quả khác lần 1 (không idempotent).
        [HttpPost("{playerId:guid}/daily-reward")]
        [ProducesResponseType(typeof(PlayerProfileResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<PlayerProfileResponse>> ClaimDailyReward(Guid playerId)
        {
            // Include: nạp luôn túi đồ của người chơi (JOIN sang bảng InventoryItems), vì sắp thêm potion vào đó.
            PlayerModel? player = await _db.Players
                .Include(p => p.Inventory)
                .FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null)
            {
                return NotFound();
            }

            // Dùng giờ UTC để "một ngày" là như nhau với mọi người chơi, ở bất kỳ múi giờ nào.
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

            // ⚠ CHƯA AN TOÀN khi nhiều request đến cùng lúc: "kiểm tra" và "cộng thưởng" không còn được bảo vệ.
            // lock trong RAM không dùng được nữa (mỗi request đọc ra một object PlayerModel RIÊNG từ database).
            // Bước 4 sẽ sửa bằng cách để chính database đảm bảo chỉ một request thắng.
            if (player.LastDailyRewardDate == today)
            {
                return Problem(
                    title: "Daily reward already claimed",
                    detail: "Come back tomorrow (UTC).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            player.Gold += DailyRewardGold;
            player.LastDailyRewardDate = today;

            InventoryItemModel? potion = player.Inventory.FirstOrDefault(i => i.ItemId == DailyRewardItemId);
            if (potion == null)
            {
                // Thêm vào danh sách của player: EF tự hiểu đây là một dòng MỚI cần INSERT vào InventoryItems.
                player.Inventory.Add(new InventoryItemModel { ItemId = DailyRewardItemId, Quantity = 1 });
            }
            else
            {
                potion.Quantity += 1;
            }

            // Một lần SaveChangesAsync = một transaction: cộng vàng, ghi ngày, thêm potion cùng thành công hoặc cùng thất bại.
            await _db.SaveChangesAsync();

            return Ok(ToProfileResponse(player));
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
