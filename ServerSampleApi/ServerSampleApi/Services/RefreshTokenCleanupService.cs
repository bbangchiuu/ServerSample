using ServerSampleApi.Auth;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Định kỳ xoá các refresh token đã hết hạn, để bảng "RefreshTokens" không phình to mãi
    /// (mỗi người chơi đang online thêm một dòng mỗi lần làm mới token, khoảng 15 phút một lần).
    ///
    /// Giống LeaderboardStartupRebuildService (chạy ngầm, bắt mọi lỗi), nhưng LẶP LẠI mãi chứ không chạy một lần:
    /// chạy ngay khi server khởi động, rồi cứ mỗi RefreshTokens:CleanupIntervalHours giờ chạy lại.
    ///
    /// Chạy nhiều server cùng lúc thì server nào cũng dọn: không sao cả, xoá token đã bị server khác xoá thì chỉ là xoá 0 dòng.
    /// </summary>
    public class RefreshTokenCleanupService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<RefreshTokenCleanupService> _logger;

        public RefreshTokenCleanupService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<RefreshTokenCleanupService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Test tắt bước này: lúc server ảo khởi động, database test chưa chạy migration, chưa có bảng RefreshTokens.
            if (!_configuration.GetValue("RefreshTokens:CleanupEnabled", defaultValue: true))
            {
                return;
            }

            TimeSpan interval = TimeSpan.FromHours(_configuration.GetValue("RefreshTokens:CleanupIntervalHours", defaultValue: 24.0));

            // Nhường luồng ngay lập tức, để phần còn lại chắc chắn chạy ngầm và server mở cổng luôn.
            await Task.Yield();

            // PeriodicTimer: "đồng hồ báo thức" lặp lại. WaitForNextTickAsync chờ tới lần reo tiếp theo
            // (không giữ thread trong lúc chờ), trả về false hoặc ném OperationCanceledException khi server tắt.
            using var timer = new PeriodicTimer(interval);
            try
            {
                do
                {
                    await CleanupOnceAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Server đang tắt: dừng vòng lặp, không phải lỗi.
            }
        }

        private async Task CleanupOnceAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Hosted service sống suốt đời server, còn RefreshTokenService (Scoped) dùng GameDbContext:
                // mỗi lần dọn tạo một scope mới, xong thì bỏ, giống như một request.
                using IServiceScope scope = _scopeFactory.CreateScope();
                RefreshTokenService refreshTokens = scope.ServiceProvider.GetRequiredService<RefreshTokenService>();

                int deleted = await refreshTokens.DeleteExpiredAsync(stoppingToken);
                _logger.LogInformation("Deleted {DeletedCount} expired refresh tokens", deleted);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Bắt MỌI lỗi (PostgreSQL tắt...). Lỗi lọt ra ngoài thì vòng lặp dừng hẳn và ASP.NET Core tắt cả server.
                // Ở đây chỉ ghi cảnh báo, lần sau sẽ thử lại.
                _logger.LogWarning(ex, "Could not delete expired refresh tokens. Will retry next time.");
            }
        }
    }
}
