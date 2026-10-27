using System.ComponentModel.DataAnnotations;
using ServerSample.Api.Data;

namespace ServerSample.Api.Players;

public sealed record ChangeDisplayNameRequest(
    [property: Required]
    [property: StringLength(20, MinimumLength = 3)]
    string DisplayName);

public sealed record PlayerProfileResponse(Guid Id, string DisplayName, int Level, long Gold, DateOnly? LastDailyRewardDate)
{
    /// <summary>Gọi trong lock(player.SyncRoot) để đọc được trạng thái nhất quán.</summary>
    public static PlayerProfileResponse From(Player player) =>
        new(player.Id, player.DisplayName, player.Level, player.Gold, player.LastDailyRewardDate);
}

// Dùng danh sách thay vì Dictionary: JsonUtility của Unity không đọc được Dictionary.
public sealed record InventoryItem(string ItemId, int Quantity);

public sealed record InventoryResponse(IReadOnlyList<InventoryItem> Items)
{
    /// <summary>Gọi trong lock(player.SyncRoot).</summary>
    public static InventoryResponse From(Player player) =>
        new(player.Inventory.Select(pair => new InventoryItem(pair.Key, pair.Value)).OrderBy(item => item.ItemId).ToList());
}
