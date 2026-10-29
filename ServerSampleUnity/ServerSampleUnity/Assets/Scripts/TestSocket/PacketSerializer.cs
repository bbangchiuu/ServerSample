using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Bản cho Unity (C# 9, .NET Standard 2.1) của ServerSample.Shared/PacketSerializer.cs.
/// Định dạng gói phải giống hệt bên server:
///
///   [ 4 byte: độ dài payload (int32, little-endian) ][ 1 byte: MessageType ][ payload ]
///
/// Mọi await dùng ConfigureAwait(false): không cần quay về main thread của Unity (xem GameClient).
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
            await stream.WriteAsync(buffer.AsMemory(0, frameSize), ct).ConfigureAwait(false);
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
        int read = await ReadUntilFullAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0)
            return null;
        if (read < HeaderSize)
            throw new EndOfStreamException("Connection closed in the middle of a packet header.");

        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength < 0 || payloadLength > maxPayloadSize)
            throw new InvalidDataException($"Invalid payload length {payloadLength} (max {maxPayloadSize}).");

        var type = (MessageType)header[4];
        byte[] payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
        if (await ReadUntilFullAsync(stream, payload, ct).ConfigureAwait(false) < payloadLength)
            throw new EndOfStreamException("Connection closed in the middle of a packet payload.");

        return new CPacket(type, payload);
    }

    /// <summary>
    /// Thay cho ReadAtLeastAsync / ReadExactlyAsync (.NET 7+, Unity chưa có).
    /// Một lần ReadAsync có thể chỉ trả về một phần dữ liệu, nên phải lặp cho tới khi đầy buffer
    /// hoặc bên kia đóng kết nối. Trả về số byte thực sự đọc được.
    /// </summary>
    private static async ValueTask<int> ReadUntilFullAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return total;
    }
}
