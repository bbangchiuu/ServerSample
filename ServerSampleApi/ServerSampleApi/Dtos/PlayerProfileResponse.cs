namespace ServerSampleApi.Dtos
{
    /// <summary>Thông tin người chơi trả về cho client.</summary>
    public class PlayerProfileResponse
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";

        public int Level { get; set; }

        public long Gold { get; set; }

        /// <summary>Client dùng để hiện nút "Nhận thưởng" hay "Đã nhận hôm nay".</summary>
        public DateOnly? LastDailyRewardDate { get; set; }
    }
}
