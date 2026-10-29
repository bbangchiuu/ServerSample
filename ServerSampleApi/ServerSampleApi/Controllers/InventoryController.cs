using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;
using ServerSampleApi.Services;

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
        private readonly PlayerStore _playerStore;

        public InventoryController(PlayerStore playerStore)
        {
            _playerStore = playerStore;
        }

        // GET /api/players/{playerId}/inventory
        [HttpGet]
        [ProducesResponseType(typeof(InventoryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<InventoryResponse> GetInventory(Guid playerId)
        {
            PlayerModel? player = _playerStore.Find(playerId);
            if (player == null)
            {
                return NotFound();
            }

            lock (player.SyncRoot)
            {
                return Ok(ToInventoryResponse(player));
            }
        }

        // POST /api/players/{playerId}/inventory/{itemId}/use
        // POST vì đây là một hành động: mỗi lần gọi tiêu hao 1 vật phẩm, gọi 2 lần là mất 2 cái.
        [HttpPost("{itemId}/use")]
        [ProducesResponseType(typeof(InventoryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public ActionResult<InventoryResponse> UseItem(Guid playerId, string itemId)
        {
            PlayerModel? player = _playerStore.Find(playerId);
            if (player == null)
            {
                return NotFound();
            }

            // Lại là "kiểm tra rồi mới làm" (còn vật phẩm không → trừ đi), nên phải nằm chung một lock,
            // nếu không 2 request cùng lúc có thể dùng được 2 lần chỉ với 1 vật phẩm.
            lock (player.SyncRoot)
            {
                if (!player.Inventory.TryGetValue(itemId, out int quantity) || quantity <= 0)
                {
                    return Problem(
                        title: "Item not owned",
                        detail: $"The player has no '{itemId}'.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                if (quantity == 1)
                {
                    // Dùng hết thì xoá hẳn khỏi túi, để túi đồ không còn dòng "potion: 0".
                    player.Inventory.Remove(itemId);
                }
                else
                {
                    player.Inventory[itemId] = quantity - 1;
                }

                // Chưa có hiệu ứng (hồi máu...): bước này chỉ trừ số lượng.
                return Ok(ToInventoryResponse(player));
            }
        }

        /// <summary>Chuyển túi đồ thành DTO, sắp xếp theo mã vật phẩm cho dễ đọc. Gọi bên trong lock.</summary>
        private static InventoryResponse ToInventoryResponse(PlayerModel player)
        {
            var response = new InventoryResponse();
            foreach (KeyValuePair<string, int> item in player.Inventory.OrderBy(pair => pair.Key))
            {
                response.Items.Add(new InventoryItemResponse
                {
                    ItemId = item.Key,
                    Quantity = item.Value,
                });
            }

            return response;
        }
    }
}
