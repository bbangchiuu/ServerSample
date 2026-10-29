#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Client TCP. Bản cho Unity (C# 9, .NET Standard 2.1) của ServerSample.Client/GameClient.cs.
/// Đây là class C# thường, KHÔNG phải MonoBehaviour: SocketController dùng nó để kết nối.
///
/// Mọi await trong class này dùng ConfigureAwait(false), nên các vòng lặp chạy trên thread nền
/// thay vì chờ tới frame tiếp theo của Unity. Nhờ vậy Ping vẫn được gửi đều kể cả khi game đang
/// load nặng hay Editor mất focus. Đổi lại: sự kiện PacketReceived / Disconnected được gọi trên
/// thread nền, KHÔNG được động vào GameObject trong đó (SocketController lo việc chuyển về main thread).
/// </summary>
public sealed class GameClient : IAsyncDisposable
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(5);

    private const int MaxPayloadSize = 64 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private Task _receiveTask = Task.CompletedTask;
    private Task _pingTask = Task.CompletedTask;
    private volatile bool _connected;

    /// <summary>Gọi trên thread nền.</summary>
    public event Action<CPacket>? PacketReceived;

    /// <summary>Gọi trên thread nền.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _connected;

    public async Task ConnectAsync(string host, int port, bool sendHeartbeat, CancellationToken ct = default)
    {
        // DisposeAsync() trong lúc đang kết nối cũng phải hủy được việc kết nối.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        // Unity chưa có TcpClient.ConnectAsync(host, port, ct) (.NET 5+), nên tự phân giải tên miền
        // rồi thử lần lượt từng địa chỉ. Mỗi địa chỉ dùng socket đúng loại (IPv4 hoặc IPv6),
        // nhờ vậy kết nối được cả trên mạng di động chỉ có IPv6.
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            linked.Token.ThrowIfCancellationRequested();
            var tcp = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                // ConnectAsync này không nhận token: khi bị hủy thì đóng socket để nó dừng lại.
                using (linked.Token.Register(() => tcp.Dispose()))
                    await tcp.ConnectAsync(address, port).ConfigureAwait(false);

                _tcp = tcp;
                break;
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                lastError = ex;
            }
        }

        linked.Token.ThrowIfCancellationRequested();
        if (_tcp is null)
            throw new IOException($"Cannot connect to {host}:{port}.", lastError);

        _stream = _tcp.GetStream();
        _connected = true;

        _receiveTask = ReceiveLoopAsync(_cts.Token);
        if (sendHeartbeat)
            _pingTask = PingLoopAsync(_cts.Token);
    }

    public async Task SendAsync(CPacket packet, CancellationToken ct = default)
    {
        NetworkStream stream = _stream ?? throw new InvalidOperationException("Not connected.");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PacketSerializer.WriteAsync(stream, packet, ct).ConfigureAwait(false);
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
                CPacket? packet = await PacketSerializer.ReadAsync(_stream!, MaxPayloadSize, ct).ConfigureAwait(false);
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
        // Unity chưa có PeriodicTimer (.NET 6+): dùng Task.Delay là đủ cho heartbeat.
        try
        {
            while (_connected)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                await SendAsync(CPacket.Empty(MessageType.Ping), ct).ConfigureAwait(false);
            }
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
        // Unity chưa có CancelAsync (.NET 8+).
        _cts.Cancel();
        _tcp?.Dispose();
        await Task.WhenAll(_receiveTask, _pingTask).ConfigureAwait(false);
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
