using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Data;
using StackExchange.Redis;

namespace ServerSampleApi.Services
{
    /// <summary>Một dòng trên bảng xếp hạng, đọc từ Redis.</summary>
    public record LeaderboardEntry(Guid PlayerId, long Gold, long Rank);

    /// <summary>
    /// Bảng xếp hạng vàng, lưu trong Redis bằng Sorted Set (các lệnh ZADD, ZREVRANGE, ZREVRANK đã thử bằng redis-cli).
    ///  - Key:    "leaderboard:gold"
    ///  - Member: playerId (Guid). Không dùng DisplayName vì tên đổi được và có thể trùng nhau.
    ///  - Score:  số vàng.
    ///
    /// Số vàng GỐC vẫn nằm trong PostgreSQL. Redis chỉ giữ một BẢN SAO để xếp hạng cho nhanh:
    /// mất dữ liệu Redis thì dựng lại được từ PostgreSQL (RebuildAsync), còn mất PostgreSQL là mất thật.
    ///
    /// Đăng ký Scoped (mỗi request một object) vì nó dùng GameDbContext, mà GameDbContext cũng là Scoped.
    /// Kết nối Redis (IConnectionMultiplexer) thì vẫn là Singleton dùng chung, nên tạo object này rất nhẹ.
    /// </summary>
    public class LeaderboardService
    {
        /// <summary>Mỗi lần gửi sang Redis tối đa 1000 người, không gửi từng người một.</summary>
        private const int RebuildBatchSize = 1000;

        private readonly IDatabase _redis;
        private readonly GameDbContext _db;
        private readonly ILogger<LeaderboardService> _logger;
        private readonly string _key;

        public LeaderboardService(
            IConnectionMultiplexer redis,
            GameDbContext db,
            IConfiguration configuration,
            ILogger<LeaderboardService> logger)
        {
            // GetDatabase(): lấy "cửa" để gửi lệnh. Rất nhẹ, không mở kết nối mới.
            _redis = redis.GetDatabase();
            _db = db;
            _logger = logger;

            // Cho phép đổi tên key qua cấu hình: test dùng key riêng để không đụng bảng xếp hạng thật.
            _key = configuration["Leaderboard:RedisKey"] ?? "leaderboard:gold";
        }

        /// <summary>
        /// Ghi số vàng hiện tại của người chơi. Redis: ZADD leaderboard:gold {gold} {playerId}
        ///
        /// Dùng ZADD (ĐẶT số vàng) chứ không dùng ZINCRBY (CỘNG thêm): số liệu luôn lấy từ PostgreSQL,
        /// nên gọi lại bao nhiêu lần cũng ra cùng kết quả. Nếu dùng ZINCRBY mà lỡ gọi 2 lần
        /// (ví dụ thử lại khi mạng lỗi) thì bảng xếp hạng sẽ bị cộng dư.
        /// </summary>
        public async Task SetGoldAsync(Guid playerId, long gold)
        {
            await _redis.SortedSetAddAsync(_key, playerId.ToString(), gold);
        }

        /// <summary>Top N người nhiều vàng nhất. Redis: ZREVRANGE leaderboard:gold 0 {count-1} WITHSCORES</summary>
        public async Task<List<LeaderboardEntry>> GetTopAsync(int count)
        {
            // Order.Descending = chữ "REV": điểm cao đứng trước.
            SortedSetEntry[] entries = await _redis.SortedSetRangeByRankWithScoresAsync(
                _key, start: 0, stop: count - 1, order: Order.Descending);

            // Redis đếm hạng từ 0, người chơi thì quen hạng 1, nên cộng thêm 1.
            return entries
                .Select((entry, index) => new LeaderboardEntry(
                    PlayerId: Guid.Parse(entry.Element.ToString()),
                    Gold: (long)entry.Score,
                    Rank: index + 1))
                .ToList();
        }

        /// <summary>
        /// Hạng và số vàng của một người chơi, hoặc null nếu người đó chưa có trên bảng.
        /// Redis: ZREVRANK + ZSCORE.
        /// </summary>
        public async Task<LeaderboardEntry?> GetPlayerAsync(Guid playerId)
        {
            string member = playerId.ToString();

            // Gửi 2 lệnh cùng lúc rồi mới chờ, thay vì chờ xong lệnh này mới gửi lệnh kia.
            Task<long?> rankTask = _redis.SortedSetRankAsync(_key, member, Order.Descending);
            Task<double?> scoreTask = _redis.SortedSetScoreAsync(_key, member);
            long? rank = await rankTask;
            double? score = await scoreTask;

            // Giống "(nil)" bạn thấy trong redis-cli khi gõ sai tên "cuong".
            if (rank == null || score == null)
            {
                return null;
            }

            return new LeaderboardEntry(playerId, (long)score.Value, rank.Value + 1);
        }

        /// <summary>
        /// Dựng lại TOÀN BỘ bảng xếp hạng từ PostgreSQL (dữ liệu gốc). Trả về số người chơi đã nạp vào.
        ///
        /// Không xoá key cũ rồi nạp dần vào, vì trong lúc nạp, ai xem bảng xếp hạng sẽ thấy bảng
        /// trống hoặc thiếu người. Thay vào đó:
        ///  1. Nạp hết vào một key TẠM, ví dụ "leaderboard:gold:rebuild:3f2a..."
        ///  2. Đổi tên key tạm thành "leaderboard:gold" (RENAME). Lệnh RENAME là nguyên tử: key cũ bị thay
        ///     bằng key mới trong MỘT bước, người xem chỉ thấy bảng cũ hoặc bảng mới, không bao giờ thấy bảng dở dang.
        /// </summary>
        public async Task<int> RebuildAsync()
        {
            // Mỗi lần dựng lại dùng một key tạm khác nhau, để 2 lần dựng lại cùng lúc không ghi chồng lên nhau.
            string tempKey = $"{_key}:rebuild:{Guid.NewGuid():N}";
            int playerCount = 0;

            try
            {
                List<SortedSetEntry> batch = new(RebuildBatchSize);

                // AsAsyncEnumerable: đọc TỪNG DÒNG từ PostgreSQL khi cần, không tải cả bảng Players vào RAM
                // một lần. Có 1 triệu người chơi thì RAM vẫn chỉ giữ khoảng 1000 người (một batch) mỗi lúc.
                // SQL: SELECT "Id", "Gold" FROM "Players" WHERE "Gold" > 0
                var players = _db.Players
                    .Where(p => p.Gold > 0)
                    .Select(p => new { p.Id, p.Gold })
                    .AsAsyncEnumerable();

                await foreach (var player in players)
                {
                    batch.Add(new SortedSetEntry(player.Id.ToString(), player.Gold));
                    if (batch.Count == RebuildBatchSize)
                    {
                        // Một lệnh ZADD chứa 1000 cặp (score, member), nhanh hơn rất nhiều so với 1000 lệnh ZADD.
                        await _redis.SortedSetAddAsync(tempKey, batch.ToArray());
                        playerCount += batch.Count;
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await _redis.SortedSetAddAsync(tempKey, batch.ToArray());
                    playerCount += batch.Count;
                }

                if (playerCount == 0)
                {
                    // Không ai có vàng: key tạm chưa từng được tạo (Redis không có Sorted Set rỗng),
                    // nên không RENAME được. Bảng xếp hạng đúng lúc này là bảng trống, tức là xoá key.
                    await _redis.KeyDeleteAsync(_key);
                }
                else
                {
                    await _redis.KeyRenameAsync(tempKey, _key); // RENAME tempKey leaderboard:gold
                }

                return playerCount;
            }
            finally
            {
                // Lỡ có lỗi giữa chừng (mất kết nối PostgreSQL...) thì xoá key tạm, không để rác lại trong Redis.
                // Nếu đã RENAME thành công thì key tạm không còn nữa, lệnh này không làm gì cả.
                await _redis.KeyDeleteAsync(tempKey);
            }
        }

        /// <summary>
        /// Gọi lúc server khởi động: Redis chưa có bảng xếp hạng (mới cài Redis, lỡ xoá key, mất dữ liệu...)
        /// thì tự dựng lại từ PostgreSQL. Redis lỗi thì chỉ ghi log cảnh báo, server vẫn khởi động bình thường.
        /// </summary>
        public async Task RebuildIfMissingAsync()
        {
            try
            {
                // EXISTS leaderboard:gold
                if (await _redis.KeyExistsAsync(_key))
                {
                    return;
                }

                int playerCount = await RebuildAsync();
                _logger.LogInformation("Leaderboard rebuilt from PostgreSQL with {PlayerCount} players", playerCount);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                _logger.LogWarning(ex, "Could not rebuild leaderboard on startup");
            }
        }
    }
}
