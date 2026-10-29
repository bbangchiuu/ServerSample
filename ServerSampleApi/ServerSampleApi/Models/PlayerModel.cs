namespace ServerSampleApi.Models
{
    /// <summary>
    /// Dữ liệu game của một người chơi. Dùng chung Id với Account: 1 tài khoản = 1 nhân vật.
    /// Tách riêng khỏi Account: thông tin đăng nhập (bảo mật) không nằm chung với dữ liệu game.
    ///
    /// Nhiều request có thể đọc/sửa CÙNG một Player cùng lúc (bấm nút 2 lần, mở game trên 2 máy...),
    /// nên mọi chỗ đọc hoặc sửa Player đều phải nằm trong lock (player.SyncRoot).
    /// </summary>
    public class PlayerModel
    {
        /// <summary>
        /// "Chìa khoá" riêng của người chơi này: lock(player.SyncRoot) chỉ chặn các request
        /// cùng đụng tới người chơi NÀY, người chơi khác vẫn được xử lý song song bình thường.
        /// Kiểu Lock (có từ .NET 9) dùng với câu lệnh lock nhanh hơn dùng object thường.
        /// </summary>
        public Lock SyncRoot { get; } = new();

        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";

        public int Level { get; set; } = 1;

        public long Gold { get; set; }

        /// <summary>Ngày (theo giờ UTC) gần nhất đã nhận thưởng hằng ngày. null = chưa nhận lần nào.</summary>
        public DateOnly? LastDailyRewardDate { get; set; }

        /// <summary>
        /// Túi đồ: mã vật phẩm → số lượng. Ví dụ { "potion": 3 }.
        /// Không phân biệt hoa thường: "potion" và "Potion" là cùng một vật phẩm.
        /// Dictionary thường KHÔNG an toàn khi nhiều thread cùng sửa, nên luôn đọc/sửa trong lock(SyncRoot).
        /// </summary>
        public Dictionary<string, int> Inventory { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
