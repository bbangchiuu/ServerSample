using ServerSampleApi.Dtos;

namespace ServerSampleApi.Hubs
{
    /// <summary>
    /// Các hàm phía CLIENT mà server gọi được (server → client).
    ///
    /// Không phải code chạy trên server: server gọi Clients.All.ReceiveChatMessage(...), SignalR gửi xuống client
    /// một gói tin kiểu {"type":1,"target":"ReceiveChatMessage","arguments":[{...}]}, và client chạy hàm nào
    /// đã đăng ký với tên "ReceiveChatMessage".
    ///
    /// Khai báo bằng interface (strongly-typed hub) thay vì Clients.All.SendAsync("ReceiveChatMessage", ...):
    /// gõ sai tên hàm thì trình biên dịch báo lỗi ngay, không âm thầm gửi tới một hàm không ai nghe.
    /// </summary>
    public interface IGameClient
    {
        Task ReceiveChatMessage(ChatMessage message);

        /// <summary>
        /// Profile của CHÍNH người chơi vừa thay đổi (nhận thưởng, đổi tên). Chỉ gửi cho các kết nối của người đó:
        /// đổi tên trên điện thoại thì máy tính đang mở game cũng thấy tên mới ngay, không cần tải lại.
        /// </summary>
        Task ProfileChanged(PlayerProfileResponse profile);

        /// <summary>Một người chơi vừa đổi số vàng trên bảng xếp hạng. Gửi cho TẤT CẢ người đang online.</summary>
        Task LeaderboardChanged(LeaderboardEntryResponse entry);

        /// <summary>Một người chơi vừa online (mở kết nối ĐẦU TIÊN). Gửi cho mọi người khác.</summary>
        Task PlayerOnline(OnlinePlayer player);

        /// <summary>Một người chơi vừa offline (đóng kết nối CUỐI CÙNG). Gửi cho mọi người khác.</summary>
        Task PlayerOffline(OnlinePlayer player);
    }
}
