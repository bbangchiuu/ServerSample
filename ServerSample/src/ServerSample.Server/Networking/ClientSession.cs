using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Đại diện cho MỘT client đang kết nối.
///
/// Mỗi session chạy 2 vòng lặp song song:
///  - ReceiveLoop: đọc gói từ socket rồi giao cho PacketDispatcher xử lý.
///  - SendLoop: lấy gói từ hàng đợi (Channel) rồi ghi ra socket.
///
/// Tại sao cần hàng đợi gửi? NetworkStream KHÔNG an toàn khi nhiều thread cùng ghi một lúc.
/// Khi broadcast chat, nhiều session khác nhau sẽ gọi Send() cùng lúc vào session này.
/// Channel gom tất cả lại, và chỉ SendLoop là nơi duy nhất ghi ra socket.
/// </summary>
public sealed class ClientSession
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ServerOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<Packet> _sendQueue;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _lastReceivedAt = Environment.TickCount64;
    private string? _closeReason;

    public ClientSession(long id, TcpClient client, ServerOptions options, ILogger logger)
    {
        Id = id;
        _client = client;
        _stream = client.GetStream();
        _options = options;
        _logger = logger;
        RemoteEndPoint = client.Client.RemoteEndPoint;

        _sendQueue = Channel.CreateBounded<Packet>(new BoundedChannelOptions(options.SendQueueCapacity)
        {
            SingleReader = true,                 // chỉ SendLoop đọc
            SingleWriter = false,                // nhiều nơi có thể gọi Send()
            FullMode = BoundedChannelFullMode.Wait, // để TryWrite trả về false khi đầy
        });
    }

    public long Id { get; }

    public EndPoint? RemoteEndPoint { get; }

    public bool IsClosed => Volatile.Read(ref _closeReason) is not null;

    /// <summary>Thời gian kể từ gói cuối cùng nhận được. Dùng cho heartbeat.</summary>
    public TimeSpan IdleTime => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastReceivedAt));

    /// <summary>Hoàn thành khi session đã đóng hẳn. Dùng để chờ khi tắt server.</summary>
    public Task Completion => _completion.Task;

    public async Task RunAsync(PacketDispatcher dispatcher)
    {
        try
        {
            Task receive = ReceiveLoopAsync(dispatcher, _cts.Token);
            Task send = SendLoopAsync(_cts.Token);
            await Task.WhenAll(receive, send);
        }
        finally
        {
            _client.Dispose();
            _cts.Dispose();
            _logger.LogInformation("Session {SessionId} ({RemoteEndPoint}) closed: {Reason}", Id, RemoteEndPoint, _closeReason);
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Đưa gói vào hàng đợi gửi. Không chờ, an toàn khi gọi từ nhiều thread.
    /// </summary>
    public void Send(Packet packet)
    {
        if (_sendQueue.Writer.TryWrite(packet))
            return;

        // TryWrite thất bại vì 2 lý do: session đã đóng (bỏ qua), hoặc hàng đợi đầy.
        // Hàng đợi đầy nghĩa là client đọc không kịp, nếu cứ giữ lại thì RAM server sẽ phình ra.
        if (!IsClosed)
            Close("send queue full (client too slow)");
    }

    /// <summary>
    /// Yêu cầu đóng session. Các gói đã nằm trong hàng đợi (ví dụ thông báo lý do) vẫn được gửi nốt,
    /// tối đa 2 giây, sau đó cắt hẳn.
    /// </summary>
    public void Close(string reason)
    {
        // Chỉ lần Close đầu tiên có tác dụng.
        if (Interlocked.CompareExchange(ref _closeReason, reason, null) is not null)
            return;

        _sendQueue.Writer.TryComplete();
        try
        {
            _cts.CancelAfter(TimeSpan.FromSeconds(2));
        }
        catch (ObjectDisposedException)
        {
            // RunAsync đã kết thúc trước đó.
        }
    }

    private async Task ReceiveLoopAsync(PacketDispatcher dispatcher, CancellationToken ct)
    {
        try
        {
            while (!IsClosed)
            {
                Packet? packet = await PacketSerializer.ReadAsync(_stream, _options.MaxPayloadSize, ct);
                if (packet is null)
                {
                    Close("client disconnected");
                    break;
                }

                Interlocked.Exchange(ref _lastReceivedAt, Environment.TickCount64);
                await dispatcher.HandleAsync(this, packet.Value);
            }
        }
        catch (OperationCanceledException)
        {
            // Session đang đóng.
        }
        catch (InvalidDataException ex)
        {
            Close($"protocol error: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close($"connection lost: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Lỗi trong code xử lý gói (bug của mình). Log đầy đủ, chỉ ngắt client này, server vẫn chạy.
            _logger.LogError(ex, "Unhandled error while handling packet from session {SessionId}", Id);
            Close("internal server error");
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            // ReadAllAsync kết thúc khi hàng đợi đã Complete (do Close) VÀ đã gửi hết gói còn lại.
            await foreach (Packet packet in _sendQueue.Reader.ReadAllAsync(ct))
                await PacketSerializer.WriteAsync(_stream, packet, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Close($"connection lost: {ex.Message}");
        }
        finally
        {
            // Gửi xong (hoặc lỗi) thì dừng luôn ReceiveLoop.
            Close("send loop ended");
            await _cts.CancelAsync();
        }
    }
}
