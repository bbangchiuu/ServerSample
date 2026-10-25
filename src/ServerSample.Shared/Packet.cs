using System.Text;

namespace ServerSample.Shared;

/// <summary>
/// Một gói tin đã được tách ra khỏi luồng TCP.
/// Payload ở giai đoạn này là UTF-8 text cho dễ debug; giai đoạn 5 sẽ đổi sang MessagePack.
/// </summary>
public readonly record struct Packet(MessageType Type, byte[] Payload)
{
    public string PayloadAsText => Encoding.UTF8.GetString(Payload);

    public static Packet FromText(MessageType type, string text) => new(type, Encoding.UTF8.GetBytes(text));

    public static Packet Empty(MessageType type) => new(type, []);
}
