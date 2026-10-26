using System;
using System.Text;

/// <summary>
/// Một gói tin. Bản cho Unity của ServerSample.Shared/CPacket.cs.
/// Unity dùng C# 9 nên chưa có "record struct" (C# 10): viết thành readonly struct thường.
/// </summary>
public readonly struct CPacket
{
    public CPacket(MessageType type, byte[] payload)
    {
        Type = type;
        Payload = payload;
    }

    public MessageType Type { get; }

    public byte[] Payload { get; }

    public string PayloadAsText => Encoding.UTF8.GetString(Payload);

    public static CPacket FromText(MessageType type, string text) => new CPacket(type, Encoding.UTF8.GetBytes(text));

    // Payload phải là mảng rỗng, KHÔNG được để null: PacketSerializer dùng Payload.Length.
    public static CPacket Empty(MessageType type) => new CPacket(type, Array.Empty<byte>());
}
