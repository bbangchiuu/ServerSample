using System.Buffers;
using System.Buffers.Binary;

namespace ServerSample.Shared;

/// <summary>
/// Đóng gói / tách gói tin trên một Stream (NetworkStream).
///
/// TCP là một DÒNG BYTE, không phải dòng "tin nhắn": một lần Read có thể nhận nửa gói,
/// hoặc 3 gói dính liền nhau. Vì vậy mỗi gói phải tự mô tả độ dài của nó (length-prefix framing):
///
///   [ 4 byte: độ dài payload (int32, little-endian) ][ 1 byte: MessageType ][ payload ]
/// </summary>
public static class PacketSerializer
{
    public const int HeaderSize = 5;

    public static async ValueTask WriteAsync(Stream stream, CPacket packet, CancellationToken ct = default)
    {
        int frameSize = HeaderSize + packet.Payload.Length;

        // Mượn buffer từ pool để không cấp phát mảng mới cho mỗi gói gửi đi.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, packet.Payload.Length);
            buffer[4] = (byte)packet.Type;
            packet.Payload.CopyTo(buffer, HeaderSize);

            // Ghi header + payload trong MỘT lần gọi để tránh gửi 2 segment TCP nhỏ.
            await stream.WriteAsync(buffer.AsMemory(0, frameSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Đọc đúng một gói. Trả về null nếu bên kia đóng kết nối "sạch" (giữa hai gói).
    /// </summary>
    /// <exception cref="InvalidDataException">Header không hợp lệ (gói quá lớn / độ dài âm).</exception>
    /// <exception cref="EndOfStreamException">Kết nối bị đóng giữa chừng một gói.</exception>
    public static async ValueTask<CPacket?> ReadAsync(Stream stream, int maxPayloadSize, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderSize];
        int read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, ct);
        if (read == 0)
            return null;
        if (read < HeaderSize)
            throw new EndOfStreamException("Connection closed in the middle of a packet header.");

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);

        // Không bao giờ tin độ dài do client gửi lên: nếu không kiểm tra, client gửi 2_000_000_000
        // là server cấp phát 2GB RAM.
        if (payloadLength < 0 || payloadLength > maxPayloadSize)
            throw new InvalidDataException($"Invalid payload length {payloadLength} (max {maxPayloadSize}).");

        var type = (MessageType)header[4];
        byte[] payload = payloadLength == 0 ? [] : new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, ct);

        return new CPacket(type, payload);
    }
}
