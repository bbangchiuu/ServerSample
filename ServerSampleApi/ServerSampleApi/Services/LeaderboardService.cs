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
    /// mất dữ liệu Redis thì dựng lại được từ PostgreSQL, còn mất PostgreSQL là mất thật.
    ///
    /// Đăng ký Singleton: cả server dùng chung một object, vì kết nối Redis (IConnectionMultiplexer)
    /// được thiết kế để tạo MỘT lần và dùng chung cho mọi request, mọi luồng.
    /// </summary>
    public class LeaderboardService
    {
        private readonly IDatabase _redis;
        private readonly RedisKey _key;

        public LeaderboardService(IConnectionMultiplexer redis, IConfiguration configuration)
        {
            // GetDatabase(): lấy "cửa" để gửi lệnh. Rất nhẹ, không mở kết nối mới.
            _redis = redis.GetDatabase();

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
    }
}
