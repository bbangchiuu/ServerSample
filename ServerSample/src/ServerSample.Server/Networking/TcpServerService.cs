using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Lắng nghe cổng TCP và nhận kết nối mới (accept loop).
/// Là một BackgroundService: Host tự gọi ExecuteAsync khi khởi động và hủy stoppingToken khi tắt (Ctrl+C).
/// </summary>
public sealed class TcpServerService(
    SessionManager sessions,
    PacketDispatcher dispatcher,
    IOptions<ServerOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<TcpServerService> logger) : BackgroundService
{
    private readonly ServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nghe trên IPv6 ở chế độ dual-mode: nhận được cả client IPv4 lẫn IPv6.
        // Cần cho game mobile: một số mạng di động chỉ có IPv6 (Apple bắt buộc app phải chạy được trên mạng đó).
        // Client IPv4 sẽ hiện địa chỉ dạng IPv4-mapped, ví dụ [::ffff:127.0.0.1]:54321.
        var listener = new TcpListener(IPAddress.IPv6Any, _options.Port);
        listener.Server.DualMode = true;
        listener.Start();
        logger.LogInformation("TCP server listening on port {Port}", _options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stoppingToken);
                Accept(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Server đang tắt.
        }
        finally
        {
            listener.Stop();
            await CloseAllSessionsAsync();
        }
    }

    private void Accept(TcpClient client)
    {
        if (sessions.Count >= _options.MaxConnections)
        {
            logger.LogWarning("Rejected {RemoteEndPoint}: server full ({Max} connections)", client.Client.RemoteEndPoint, _options.MaxConnections);
            client.Dispose();
            return;
        }

        // Tắt thuật toán Nagle: gửi gói nhỏ ngay lập tức thay vì gom lại. Game cần độ trễ thấp.
        client.NoDelay = true;

        var session = new ClientSession(sessions.NextId(), client, _options, loggerFactory.CreateLogger<ClientSession>());
        sessions.Add(session);
        logger.LogInformation("Session {SessionId} connected from {RemoteEndPoint} (online: {Online})", session.Id, session.RemoteEndPoint, sessions.Count);

        session.Send(CPacket.FromText(MessageType.Notice, $"Welcome! You are #{session.Id}. Online: {sessions.Count}"));

        // Không await: accept loop phải quay lại nhận client tiếp theo ngay.
        // RunSessionAsync tự bắt mọi exception nên không có exception nào bị "nuốt mất".
        _ = RunSessionAsync(session);
    }

    private async Task RunSessionAsync(ClientSession session)
    {
        try
        {
            await session.RunAsync(dispatcher);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Session {SessionId} crashed", session.Id);
        }
        finally
        {
            sessions.Remove(session);
        }
    }

    private async Task CloseAllSessionsAsync()
    {
        List<ClientSession> all = sessions.All.ToList();
        if (all.Count == 0)
            return;

        logger.LogInformation("Closing {Count} sessions...", all.Count);
        foreach (ClientSession session in all)
        {
            session.Send(CPacket.FromText(MessageType.Notice, "Server is shutting down."));
            session.Close("server shutting down");
        }

        try
        {
            await Task.WhenAll(all.Select(s => s.Completion)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Some sessions did not close within 5 seconds");
        }
    }
}
