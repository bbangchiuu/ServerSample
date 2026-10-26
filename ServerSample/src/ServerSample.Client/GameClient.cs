using System.Net.Sockets;
using ServerSample.Shared;

namespace ServerSample.Client;

/// <summary>
/// Client TCP tối giản. Cấu trúc gần giống thứ bạn sẽ viết trong Unity.
///
/// Server dùng Channel để tuần tự hóa việc ghi socket; ở client mình dùng cách thứ hai là SemaphoreSlim
/// làm "lock async" (lock thường không cho phép await bên trong). Cả hai cách đều đúng, hãy so sánh.
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private const int MaxPayloadSize = 64 * 1024;

    private readonly TcpClient _tcp = new() { NoDelay = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream? _stream;
    private Task _receiveTask = Task.CompletedTask;
    private Task _pingTask = Task.CompletedTask;
    private volatile bool _connected;

    /// <summary>Lưu ý: được gọi trên thread của vòng nhận, không phải main thread (trong Unity phải chuyển về main thread).</summary>
    public event Action<CPacket>? PacketReceived;

    public event Action<string>? Disconnected;

    public bool IsConnected => _connected;

    public async Task ConnectAsync(string host, int port, bool sendHeartbeat, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _connected = true;

        _receiveTask = ReceiveLoopAsync(_cts.Token);
        if (sendHeartbeat)
            _pingTask = PingLoopAsync(_cts.Token);
    }

    public async Task SendAsync(CPacket packet, CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Not connected.");

        await _writeLock.WaitAsync(ct);
        try
        {
            await PacketSerializer.WriteAsync(_stream, packet, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        string reason;
        try
        {
            while (true)
            {
                CPacket? packet = await PacketSerializer.ReadAsync(_stream!, MaxPayloadSize, ct);
                if (packet is null)
                {
                    reason = "server closed the connection";
                    break;
                }

                PacketReceived?.Invoke(packet.Value);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            reason = $"connection error: {ex.Message}";
        }
        catch
        {
            // Chính mình chủ động ngắt (DisposeAsync), không cần báo.
            _connected = false;
            return;
        }

        _connected = false;
        Disconnected?.Invoke(reason);
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PingInterval);
        try
        {
            while (_connected && await timer.WaitForNextTickAsync(ct))
                await SendAsync(CPacket.Empty(MessageType.Ping), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Vòng nhận sẽ phát hiện mất kết nối và báo Disconnected.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _tcp.Dispose();
        await Task.WhenAll(_receiveTask, _pingTask);
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
