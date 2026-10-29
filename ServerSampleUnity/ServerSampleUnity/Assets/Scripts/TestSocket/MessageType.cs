/// <summary>
/// Loại gói tin. Phải giống hệt ServerSample.Shared/MessageType.cs bên server.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Client -> Server: "tôi còn sống" (heartbeat).</summary>
    Ping = 1,

    /// <summary>Server -> Client: trả lời Ping.</summary>
    Pong = 2,

    /// <summary>Client -> Server: gửi gì nhận lại đúng cái đó.</summary>
    Echo = 3,

    /// <summary>Server -> Client: trả lời Echo.</summary>
    EchoReply = 4,

    /// <summary>Client -> Server: tin nhắn chat.</summary>
    Chat = 5,

    /// <summary>Server -> mọi Client: tin nhắn chat đã gắn tên người gửi.</summary>
    ChatBroadcast = 6,

    /// <summary>Server -> Client: thông báo hệ thống (chào mừng, lý do bị ngắt kết nối...).</summary>
    Notice = 7,
}
