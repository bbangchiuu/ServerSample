using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Định kỳ ngắt các client "chết".
///
/// Khi điện thoại mất sóng, tắt nguồn đột ngột, hoặc app bị treo / bị đưa xuống nền (suspend), server KHÔNG nhận được
/// tín hiệu đóng kết nối nào (app bị kill thì khác: hệ điều hành của máy client vẫn tự gửi FIN/RST),
/// socket cứ treo mãi và chiếm tài nguyên. Cách thực tế nhất là heartbeat: client gửi Ping đều đặn,
/// quá HeartbeatTimeout không thấy gì thì server tự ngắt. (TCP keepalive cũng có, nhưng mặc định khoảng 2 giờ,
/// quá chậm cho game.)
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
                    session.Send(CPacket.FromText(MessageType.Notice, "Disconnected: heartbeat timeout."));
                    session.Close("heartbeat timeout");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
