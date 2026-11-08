using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using ServerSampleApi.Hubs;
using StackExchange.Redis;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Chạy ngầm suốt đời server (giống RefreshTokenCleanupService):
    ///  - Mỗi 10 giây: báo "server này còn sống" (heartbeat), rồi dọn phần của các server đã chết
    ///    (quá 30 giây không heartbeat), báo PlayerOffline cho những người chơi vì vậy mà offline.
    ///  - Lúc server tắt BÌNH THƯỜNG: tự dọn phần của mình luôn, không bắt server khác đợi 30 giây.
    ///    Bị giết đột ngột thì không chạy được bước này, nhưng server khác (hoặc chính server này lúc khởi động
    ///    lại, với ServerId mới) sẽ dọn sau tối đa khoảng 40 giây.
    /// </summary>
    public class PresenceHeartbeatService : BackgroundService
    {
        private readonly PresenceService _presence;
        private readonly IHubContext<GameHub, IGameClient> _hub;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<PresenceHeartbeatService> _logger;

        public PresenceHeartbeatService(
            PresenceService presence,
            IHubContext<GameHub, IGameClient> hub,
            IServiceScopeFactory scopeFactory,
            ILogger<PresenceHeartbeatService> logger)
        {
            _presence = presence;
            _hub = hub;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield(); // chạy ngầm, không bắt server chờ
            _logger.LogInformation("Presence server id: {ServerId}", _presence.ServerId);

            using var timer = new PeriodicTimer(PresenceService.HeartbeatInterval);
            try
            {
                do
                {
                    await BeatAsync();
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Server đang tắt: dừng vòng lặp.
            }
        }

        /// <summary>Một nhịp: heartbeat, rồi dọn server chết. Public để test gọi trực tiếp, khỏi chờ 10 giây.</summary>
        public async Task BeatAsync()
        {
            try
            {
                await _presence.HeartbeatAsync();
                List<Guid> offline = await _presence.RemoveDeadServersAsync();
                await NotifyOfflineAsync(offline);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // Redis lỗi thì lần sau thử lại. Lỗi lọt ra ngoài thì vòng lặp dừng hẳn và ASP.NET Core tắt cả server.
                _logger.LogWarning(ex, "Presence heartbeat failed. Will retry.");
            }
        }

        /// <summary>Server tắt bình thường (Ctrl+C, deploy bản mới...): ASP.NET Core gọi StopAsync, tự dọn phần của mình.</summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                List<Guid> offline = await _presence.RemoveThisServerAsync();
                await NotifyOfflineAsync(offline);
            }
            catch (Exception ex)
            {
                // Đang tắt: dọn không được thì server khác sẽ dọn sau, chỉ ghi cảnh báo.
                _logger.LogWarning(ex, "Could not remove presence of this server on shutdown.");
            }
            await base.StopAsync(cancellationToken);
        }

        /// <summary>Báo PlayerOffline cho mọi người, kèm tên hiển thị đọc từ PostgreSQL.</summary>
        private async Task NotifyOfflineAsync(List<Guid> playerIds)
        {
            if (playerIds.Count == 0)
            {
                return;
            }

            _logger.LogInformation("{Count} players went offline because their server stopped", playerIds.Count);

            // Hosted service là Singleton, GameDbContext là Scoped: tự tạo scope (giống LeaderboardStartupRebuildService).
            using IServiceScope scope = _scopeFactory.CreateScope();
            GameDbContext db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            List<OnlinePlayer> players = await db.Players
                .Where(p => playerIds.Contains(p.Id))
                .Select(p => new OnlinePlayer { PlayerId = p.Id, DisplayName = p.DisplayName })
                .ToListAsync();

            foreach (OnlinePlayer player in players)
            {
                await _hub.Clients.All.PlayerOffline(player);
            }
        }
    }
}
