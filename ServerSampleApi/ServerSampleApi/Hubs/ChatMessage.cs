namespace ServerSampleApi.Hubs
{
    /// <summary>Một tin nhắn chat, server đẩy xuống mọi client qua IGameClient.ReceiveChatMessage.</summary>
    public class ChatMessage
    {
        public Guid PlayerId { get; set; }

        public string DisplayName { get; set; } = "";

        public string Text { get; set; } = "";

        /// <summary>Thời điểm SERVER nhận được tin (UTC). Không dùng giờ của client vì client có thể đặt sai giờ.</summary>
        public DateTime SentAt { get; set; }
    }
}
