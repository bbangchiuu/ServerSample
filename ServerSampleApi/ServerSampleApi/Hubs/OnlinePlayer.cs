namespace ServerSampleApi.Hubs
{
    /// <summary>Một người chơi đang online. Gửi kèm sự kiện PlayerOnline / PlayerOffline.</summary>
    public class OnlinePlayer
    {
        public Guid PlayerId { get; set; }

        public string DisplayName { get; set; } = "";
    }

    /// <summary>
    /// Kết quả của GameHub.GetOnlinePlayers. Bọc danh sách trong một object ({"players":[...]}) thay vì trả thẳng
    /// mảng: JsonUtility của Unity không đọc được JSON mà gốc là một mảng.
    /// </summary>
    public class OnlinePlayersResponse
    {
        public List<OnlinePlayer> Players { get; set; } = new();
    }
}
