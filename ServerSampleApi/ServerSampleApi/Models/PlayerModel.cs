namespace ServerSampleApi.Models
{
    /// <summary>
    /// Dữ liệu game của một người chơi, lưu ở bảng "Players".
    /// Dùng chung Id với Account: 1 tài khoản = 1 nhân vật.
    /// Tách riêng khỏi Account: thông tin đăng nhập (bảo mật) không nằm chung với dữ liệu game.
    /// </summary>
    public class PlayerModel
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";

        public int Level { get; set; } = 1;

        public long Gold { get; set; }

        /// <summary>Ngày (theo giờ UTC) gần nhất đã nhận thưởng hằng ngày. null = chưa nhận lần nào.</summary>
        public DateOnly? LastDailyRewardDate { get; set; }

        /// <summary>
        /// Túi đồ: các dòng của bảng "InventoryItems" thuộc về người chơi này (quan hệ một-nhiều).
        /// EF Core chỉ nạp danh sách này khi được yêu cầu (Include), bình thường nó rỗng.
        /// </summary>
        public List<InventoryItemModel> Inventory { get; set; } = new();
    }

    public class LogEventModel
    {
        public Guid Id { get; set; }
        public Guid PlayerId { get; set; }
        public string EventName { get; set; } = "";
        public string EventData { get; set; } = "";
    }
}
