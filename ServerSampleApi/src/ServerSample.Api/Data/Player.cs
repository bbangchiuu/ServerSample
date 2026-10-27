namespace ServerSample.Api.Data;

/// <summary>
/// Dữ liệu game của một người chơi. Cùng Id với Account (1 tài khoản = 1 nhân vật).
///
/// Nhiều request có thể sửa cùng một Player cùng lúc (bấm nút 2 lần, mở game trên 2 máy...),
/// nên mọi thao tác đọc/sửa phải nằm trong lock (player.SyncRoot).
/// </summary>
public sealed class Player
{
    public object SyncRoot { get; } = new();

    public required Guid Id { get; init; }

    public required string DisplayName { get; set; }

    public int Level { get; set; } = 1;

    public long Gold { get; set; }

    /// <summary>Ngày (UTC) gần nhất đã nhận thưởng hằng ngày.</summary>
    public DateOnly? LastDailyRewardDate { get; set; }

    /// <summary>Túi đồ: mã vật phẩm → số lượng.</summary>
    public Dictionary<string, int> Inventory { get; } = new();
}
