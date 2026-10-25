using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Định kỳ ngắt các client "chết".
///
/// Khi mạng điện thoại mất sóng hoặc app bị kill, server thường KHÔNG nhận được tín hiệu đóng kết nối nào,
/// socket cứ treo mãi và chiếm tài nguyên. Cách duy nhất để biết là: client phải gửi Ping đều đặn,
/// quá HeartbeatTimeout không thấy gì thì server tự ngắt.
/// </summary>
public sealed class HeartbeatMonitor(
    SessionManager sessions,
    IOptions<ServerOptions> options,
    ILogger<HeartbeatMonitor> logger) : BackgroundService
{
    private readonly ServerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(_options.HeartbeatTimeoutSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatCheckIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (ClientSession session in sessions.All)
                {
                    if (session.IsClosed || session.IdleTime <= timeout)
                        continue;

                    logger.LogInformation("Session {SessionId} idle for {Idle:N1}s, disconnecting", session.Id, session.IdleTime.TotalSeconds);
                    session.Send(Packet.FromText(MessageType.Notice, "Disconnected: heartbeat timeout."));
                    session.Close("heartbeat timeout");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
