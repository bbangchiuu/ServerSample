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
    }
}
