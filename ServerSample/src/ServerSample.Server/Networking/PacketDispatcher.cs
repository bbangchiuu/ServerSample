using Microsoft.Extensions.Options;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Nhận gói đã tách từ ClientSession và quyết định làm gì với nó.
/// Mỗi session gọi HandleAsync tuần tự (gói sau chờ gói trước xử lý xong),
/// nhưng NHIỀU session gọi song song, nên code ở đây không được giữ state chung mà không đồng bộ.
///
/// Trả về ValueTask vì các giai đoạn sau sẽ có xử lý async (đọc/ghi database).
/// </summary>
public sealed class PacketDispatcher(SessionManager sessions, IOptions<ServerOptions> options)
{
    private readonly ServerOptions _options = options.Value;

    public ValueTask HandleAsync(ClientSession session, CPacket packet)
    {
        switch (packet.Type)
        {
            case MessageType.Ping:
                session.Send(CPacket.Empty(MessageType.Pong));
                break;

            case MessageType.Echo:
                session.Send(packet with { Type = MessageType.EchoReply });
                break;

            case MessageType.Chat:
                HandleChat(session, packet.PayloadAsText);
                break;

            default:
                // Client gửi loại gói server không gửi lên được (Pong, Notice...) hoặc không tồn tại:
                // client lỗi hoặc đang thử phá, ngắt luôn.
                session.Close($"unexpected message type {(byte)packet.Type}");
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleChat(ClientSession session, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return;

        if (text.Length > _options.MaxChatLength)
        {
            session.Send(CPacket.FromText(MessageType.Notice, $"Message too long (max {_options.MaxChatLength} characters)."));
            return;
        }

        sessions.Broadcast(CPacket.FromText(MessageType.ChatBroadcast, $"[#{session.Id}] {text}"));
    }
}
