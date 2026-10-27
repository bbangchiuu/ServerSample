using Microsoft.AspNetCore.Http.HttpResults;
using ServerSample.Api.Common;
using ServerSample.Api.Data;

namespace ServerSample.Api.Players;

/// <summary>
/// API dữ liệu người chơi.
///
/// ⚠ CHƯA BẢO VỆ: ai biết playerId là đọc/sửa được dữ liệu của người đó.
/// Giai đoạn 4 (JWT) sẽ sửa: server lấy playerId từ token đăng nhập, không tin playerId client gửi lên.
///
/// Nguyên tắc: server là nguồn sự thật. KHÔNG có API nào cho client tự đặt vàng hay tự thêm vật phẩm;
/// client chỉ được YÊU CẦU một hành động (nhận thưởng, dùng vật phẩm), server tự kiểm tra và tự tính.
/// </summary>
public static class PlayerEndpoints
{
    public const int DailyRewardGold = 100;
    public const string DailyRewardItemId = "potion";

    public static void MapPlayerEndpoints(this IEndpointRouteBuilder app)
    {
        // {playerId:guid}: route constraint, chỉ khớp khi phần này là một Guid hợp lệ (không thì 404).
        RouteGroupBuilder group = app.MapGroup("/api/players/{playerId:guid}").WithTags("Players");

        group.MapGet("", GetProfile);
        group.MapPut("/display-name", ChangeDisplayName).WithValidation<ChangeDisplayNameRequest>();
        group.MapGet("/inventory", GetInventory);
        group.MapPost("/daily-reward", ClaimDailyReward);
        group.MapPost("/inventory/{itemId}/use", UseItem);
    }

    // GET /api/players/{playerId}
    private static Results<Ok<PlayerProfileResponse>, NotFound> GetProfile(Guid playerId, InMemoryDatabase db)
    {
        if (!db.Players.TryGetValue(playerId, out Player? player))
            return TypedResults.NotFound();

        lock (player.SyncRoot)
            return TypedResults.Ok(PlayerProfileResponse.From(player));
    }

    // PUT /api/players/{playerId}/display-name
    private static Results<Ok<PlayerProfileResponse>, NotFound> ChangeDisplayName(
        Guid playerId,
        ChangeDisplayNameRequest request,
        InMemoryDatabase db)
    {
        if (!db.Players.TryGetValue(playerId, out Player? player))
            return TypedResults.NotFound();

        lock (player.SyncRoot)
        {
            player.DisplayName = request.DisplayName.Trim();
            return TypedResults.Ok(PlayerProfileResponse.From(player));
        }
    }

    // GET /api/players/{playerId}/inventory
    private static Results<Ok<InventoryResponse>, NotFound> GetInventory(Guid playerId, InMemoryDatabase db)
    {
        if (!db.Players.TryGetValue(playerId, out Player? player))
            return TypedResults.NotFound();

        lock (player.SyncRoot)
            return TypedResults.Ok(InventoryResponse.From(player));
    }

    // POST /api/players/{playerId}/daily-reward
    private static Results<Ok<PlayerProfileResponse>, NotFound, ProblemHttpResult> ClaimDailyReward(
        Guid playerId,
        InMemoryDatabase db,
        TimeProvider time)
    {
        if (!db.Players.TryGetValue(playerId, out Player? player))
            return TypedResults.NotFound();

        DateOnly today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        // Kiểm tra và cộng thưởng phải nằm trong CÙNG một lock. Nếu không, 2 request gửi cùng lúc
        // (bấm 2 lần, hoặc cố tình gian lận) đều thấy "chưa nhận" và đều được cộng thưởng.
        lock (player.SyncRoot)
        {
            if (player.LastDailyRewardDate == today)
            {
                return TypedResults.Problem(
                    title: "Daily reward already claimed",
                    detail: "Come back tomorrow (UTC).",
                    statusCode: StatusCodes.Status409Conflict);
            }

            player.Gold += DailyRewardGold;
            player.Inventory[DailyRewardItemId] = player.Inventory.GetValueOrDefault(DailyRewardItemId) + 1;
            player.LastDailyRewardDate = today;
            return TypedResults.Ok(PlayerProfileResponse.From(player));
        }
    }

    // POST /api/players/{playerId}/inventory/{itemId}/use
    private static Results<Ok<InventoryResponse>, NotFound, ProblemHttpResult> UseItem(
        Guid playerId,
        string itemId,
        InMemoryDatabase db)
    {
        if (!db.Players.TryGetValue(playerId, out Player? player))
            return TypedResults.NotFound();

        lock (player.SyncRoot)
        {
            if (!player.Inventory.TryGetValue(itemId, out int quantity) || quantity <= 0)
            {
                return TypedResults.Problem(
                    title: "Item not owned",
                    detail: $"The player has no '{itemId}'.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (quantity == 1)
                player.Inventory.Remove(itemId);
            else
                player.Inventory[itemId] = quantity - 1;

            // Chưa có hiệu ứng (hồi máu...): giai đoạn này chỉ trừ số lượng.
            return TypedResults.Ok(InventoryResponse.From(player));
        }
    }
}
