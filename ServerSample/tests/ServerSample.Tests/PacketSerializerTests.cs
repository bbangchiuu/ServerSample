using ServerSample.Shared;

namespace ServerSample.Tests;

public class PacketSerializerTests
{
    private const int MaxPayload = 1024;

    [Fact]
    public async Task Roundtrip_TextPacket()
    {
        var stream = new MemoryStream();
        await PacketSerializer.WriteAsync(stream, Packet.FromText(MessageType.Chat, "xin chào"));
        stream.Position = 0;

        Packet? result = await PacketSerializer.ReadAsync(stream, MaxPayload);

        Assert.NotNull(result);
        Assert.Equal(MessageType.Chat, result.Value.Type);
        Assert.Equal("xin chào", result.Value.PayloadAsText);
    }

    [Fact]
    public async Task Roundtrip_EmptyPayload()
    {
        var stream = new MemoryStream();
        await PacketSerializer.WriteAsync(stream, Packet.Empty(MessageType.Ping));

        Assert.Equal(PacketSerializer.HeaderSize, stream.Length);

        stream.Position = 0;
        Packet? result = await PacketSerializer.ReadAsync(stream, MaxPayload);

        Assert.Equal(MessageType.Ping, result!.Value.Type);
        Assert.Empty(result.Value.Payload);
    }

    [Fact]
    public async Task ReadsPacketsStuckTogether()
    {
        // TCP có thể gửi nhiều gói dính liền trong một lần Read.
        var stream = new MemoryStream();
        for (int i = 0; i < 3; i++)
            await PacketSerializer.WriteAsync(stream, Packet.FromText(MessageType.Echo, $"msg {i}"));
        stream.Position = 0;

        for (int i = 0; i < 3; i++)
        {
            Packet? result = await PacketSerializer.ReadAsync(stream, MaxPayload);
            Assert.Equal($"msg {i}", result!.Value.PayloadAsText);
        }

        Assert.Null(await PacketSerializer.ReadAsync(stream, MaxPayload));
    }

    [Fact]
    public async Task ReadsPacketArrivingOneByteAtATime()
    {
        // ...hoặc chia một gói ra làm nhiều lần Read.
        var source = new MemoryStream();
        await PacketSerializer.WriteAsync(source, Packet.FromText(MessageType.Chat, "fragmented packet"));
        var stream = new OneByteAtATimeStream(source.ToArray());

        Packet? result = await PacketSerializer.ReadAsync(stream, MaxPayload);

        Assert.Equal("fragmented packet", result!.Value.PayloadAsText);
    }

    [Fact]
    public async Task ReturnsNull_WhenStreamEndsCleanly()
    {
        Assert.Null(await PacketSerializer.ReadAsync(new MemoryStream(), MaxPayload));
    }

    [Fact]
    public async Task Throws_WhenStreamEndsInsideHeader()
    {
        var stream = new MemoryStream([1, 0, 0]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PacketSerializer.ReadAsync(stream, MaxPayload).AsTask());
    }

    [Fact]
    public async Task Throws_WhenStreamEndsInsidePayload()
    {
        var source = new MemoryStream();
        await PacketSerializer.WriteAsync(source, Packet.FromText(MessageType.Chat, "hello"));
        var stream = new MemoryStream(source.ToArray()[..^2]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => PacketSerializer.ReadAsync(stream, MaxPayload).AsTask());
    }

    [Theory]
    [InlineData(MaxPayload + 1)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public async Task Throws_WhenDeclaredLengthIsInvalid(int declaredLength)
    {
        // Client độc hại khai báo độ dài khổng lồ: phải từ chối trước khi cấp phát bộ nhớ.
        byte[] header = new byte[PacketSerializer.HeaderSize];
        BitConverter.TryWriteBytes(header, declaredLength);
        header[4] = (byte)MessageType.Chat;

        await Assert.ThrowsAsync<InvalidDataException>(() => PacketSerializer.ReadAsync(new MemoryStream(header), MaxPayload).AsTask());
    }

    /// <summary>Stream giả lập mạng chậm: mỗi lần Read chỉ trả về đúng 1 byte.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
    }
}
