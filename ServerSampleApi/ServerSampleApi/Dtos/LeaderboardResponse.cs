namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Bảng xếp hạng trả về cho client.
    /// Bọc danh sách trong một object (giống InventoryResponse) vì JsonUtility của Unity không đọc được
    /// JSON bắt đầu bằng mảng [...].
    /// </summary>
    public class LeaderboardResponse
    {
        public List<LeaderboardEntryResponse> Entries { get; set; } = new();
    }

    public class LeaderboardEntryResponse
    {
        /// <summary>Hạng, bắt đầu từ 1.</summary>
        public long Rank { get; set; }

        public Guid PlayerId { get; set; }

        public string DisplayName { get; set; } = "";

        public long Gold { get; set; }
    }

    public class LeaderboardRebuildResponse
    {
        /// <summary>Số người chơi đã được nạp vào bảng xếp hạng.</summary>
        public int PlayerCount { get; set; }
    }
}
