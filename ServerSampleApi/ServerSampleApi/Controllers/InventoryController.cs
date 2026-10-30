using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API túi đồ của người chơi. Tách khỏi PlayersController để mỗi controller lo một việc.
    ///
    /// ⚠ CHƯA ĐƯỢC BẢO VỆ (giống PlayersController): giai đoạn 4 (JWT) sẽ sửa.
    ///
    /// Không có API nào cho client tự THÊM vật phẩm: vật phẩm chỉ đến từ những hành động
    /// server kiểm soát (nhận thưởng hằng ngày, sau này là mua trong shop...).
    /// </summary>
    [ApiController]
    // Route đặt ở cấp controller có cả tham số: mọi API trong đây đều bắt đầu bằng
    // /api/players/{playerId}/inventory, và hàm nào cũng nhận được playerId.
    [Route("api/players/{playerId:guid}/inventory")]
    public class InventoryController : ControllerBase
    {
        private readonly GameDbContext _db;

        public InventoryController(GameDbContext db)
        {
            _db = db;
        }

        // GET /api/players/{playerId}/inventory
        [HttpGet]
        [ProducesResponseType(typeof(InventoryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<InventoryResponse>> GetInventory(Guid playerId)
        {
            // AnyAsync: chỉ hỏi "có tồn tại không", không đọc cả dòng. SQL: SELECT EXISTS (...)
            if (!await _db.Players.AnyAsync(p => p.Id == playerId))
            {
                return NotFound();
            }

            return Ok(await LoadInventoryAsync(playerId));
        }

        // POST /api/players/{playerId}/inventory/{itemId}/use
        // POST vì đây là một hành động: mỗi lần gọi tiêu hao 1 vật phẩm, gọi 2 lần là mất 2 cái.
        [HttpPost("{itemId}/use")]
        [ProducesResponseType(typeof(InventoryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<InventoryResponse>> UseItem(Guid playerId, string itemId)
        {
            // Mã vật phẩm luôn lưu chữ thường, nên "Potion" và "POTION" đều là "potion".
            itemId = itemId.ToLowerInvariant();

            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Trừ 1 NẾU còn ít nhất 1 cái, trong MỘT câu lệnh:
            //   UPDATE "InventoryItems" SET "Quantity" = "Quantity" - 1
            //   WHERE "PlayerId" = @playerId AND "ItemId" = @itemId AND "Quantity" > 0
            // 10 request dùng 1 potion cùng lúc: request đầu khoá dòng và trừ xuống 0; các request sau chờ,
            // kiểm tra lại "Quantity" > 0 thì thấy sai, nên không trừ được (0 dòng), và trả 409.
            int updatedRows = await _db.InventoryItems
                .Where(i => i.PlayerId == playerId && i.ItemId == itemId && i.Quantity > 0)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(i => i.Quantity, i => i.Quantity - 1));

            if (updatedRows == 0)
            {
                // Không trừ được: hoặc người chơi không tồn tại (404), hoặc không có vật phẩm này (409).
                if (!await _db.Players.AnyAsync(p => p.Id == playerId))
                {
                    return NotFound();
                }

                return Problem(
                    title: "Item not owned",
                    detail: $"The player has no '{itemId}'.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Dùng hết thì xoá hẳn dòng này, để túi đồ không còn dòng "potion: 0".
            // ExecuteDeleteAsync: DELETE FROM "InventoryItems" WHERE ... AND "Quantity" <= 0
            await _db.InventoryItems
                .Where(i => i.PlayerId == playerId && i.ItemId == itemId && i.Quantity <= 0)
                .ExecuteDeleteAsync();

            await transaction.CommitAsync();

            // Chưa có hiệu ứng (hồi máu...): bước này chỉ trừ số lượng.
            return Ok(await LoadInventoryAsync(playerId));
        }

        /// <summary>Đọc túi đồ từ database, sắp xếp theo mã vật phẩm cho dễ đọc.</summary>
        private async Task<InventoryResponse> LoadInventoryAsync(Guid playerId)
        {
            // Select: chỉ lấy đúng 2 cột cần thiết rồi tạo thẳng DTO, không đọc cả object InventoryItemModel.
            List<InventoryItemResponse> items = await _db.InventoryItems
                .Where(i => i.PlayerId == playerId)
                .OrderBy(i => i.ItemId)
                .Select(i => new InventoryItemResponse { ItemId = i.ItemId, Quantity = i.Quantity })
                .ToListAsync();

            return new InventoryResponse { Items = items };
        }
    }
}
