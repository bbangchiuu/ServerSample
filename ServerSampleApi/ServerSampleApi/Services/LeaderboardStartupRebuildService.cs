namespace ServerSampleApi.Services
{
    /// <summary>
    /// Lúc server khởi động: Redis chưa có bảng xếp hạng (mới cài Redis, lỡ xoá key, mất dữ liệu...)
    /// thì dựng lại từ PostgreSQL. Chạy NGẦM, song song với server.
    ///
    /// Vì sao không làm thẳng trong Program.cs với "await" trước app.Run() như trước:
    /// server phải chờ việc này xong mới mở cổng. Docker tắt thì Redis không trả lời, thư viện chờ hết thời gian
    /// (5 giây kết nối + 5 giây chờ lệnh), nên server treo khoảng 10 giây. Bảng xếp hạng to thì còn lâu hơn.
    /// Việc phụ không được làm chậm việc chính (mở cổng phục vụ người chơi).
    ///
    /// BackgroundService: ASP.NET Core tự gọi ExecuteAsync một lần khi server khởi động và KHÔNG chờ nó xong.
    /// Đăng ký bằng builder.Services.AddHostedService&lt;...&gt;() trong Program.cs.
    /// </summary>
    public class LeaderboardStartupRebuildService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<LeaderboardStartupRebuildService> _logger;

        // Hosted service sống suốt đời server (giống Singleton), nên KHÔNG nhận thẳng LeaderboardService (Scoped)
        // qua constructor được. Nhận IServiceScopeFactory rồi tự tạo scope khi cần, giống như ngoài HTTP request.
        public LeaderboardStartupRebuildService(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            ILogger<LeaderboardStartupRebuildService> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Test tắt bước này (Leaderboard:RebuildOnStartup = false) vì lúc server ảo khởi động,
            // database test chưa chạy migration, chưa có bảng Players.
            if (!_configuration.GetValue("Leaderboard:RebuildOnStartup", defaultValue: true))
            {
                return;
            }

            // Nhường luồng ngay lập tức, để phần còn lại chắc chắn chạy ngầm và server mở cổng luôn.
            await Task.Yield();

            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                LeaderboardService leaderboard = scope.ServiceProvider.GetRequiredService<LeaderboardService>();

                int? playerCount = await leaderboard.RebuildIfMissingAsync();
                if (playerCount != null)
                {
                    _logger.LogInformation("Leaderboard rebuilt from PostgreSQL with {PlayerCount} players", playerCount);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Bắt MỌI lỗi: Redis tắt, PostgreSQL tắt, mất mạng... Lỗi lọt ra khỏi BackgroundService thì
                // ASP.NET Core sẽ TẮT CẢ SERVER, chỉ vì không dựng được bảng xếp hạng. Ở đây chỉ ghi cảnh báo.
                _logger.LogWarning(ex,
                    "Could not rebuild leaderboard on startup. Call POST /api/leaderboard/rebuild when Redis and PostgreSQL are up.");
            }
        }
    }
}
