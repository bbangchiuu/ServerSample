using StackExchange.Redis;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Ai đang online, lưu trong Redis bằng một Hash:
    ///   key "presence:online", mỗi field là một playerId, giá trị là SỐ KẾT NỐI đang mở của người đó.
    ///   Ví dụ: { "7c21...": 2, "a9f0...": 1 }  → Alice mở 2 thiết bị, Bob mở 1.
    ///
    /// Đếm số kết nối, không chỉ đánh dấu "có/không": Alice tắt điện thoại mà máy tính vẫn mở thì vẫn online.
    /// Bộ đếm 0 → 1: vừa online. 1 → 0: vừa offline. Chỉ hai lúc đó mới báo cho mọi người.
    ///
    /// Lưu trong Redis, không lưu trong RAM của server: chạy nhiều server thì mỗi server chỉ thấy kết nối của
    /// chính nó, còn Redis là nơi chung mà server nào cũng đọc/ghi được. Lệnh HINCRBY của Redis là nguyên tử
    /// (atomic): hai server cùng cộng một lúc cũng không bị mất lượt nào.
    ///
    /// Giới hạn: server bị tắt đột ngột (crash, mất điện) thì không kịp trừ bộ đếm, người chơi trên server đó
    /// bị "kẹt" online mãi. Cách xử lý (mỗi server định kỳ báo "tôi còn sống", hết hạn thì xoá phần của nó)
    /// để sau; ở đây chấp nhận giới hạn này cho dễ hiểu.
    ///
    /// Singleton: chỉ dùng IConnectionMultiplexer (Singleton), không dùng GameDbContext.
    /// </summary>
    public class PresenceService
    {
        /// <summary>
        /// Trừ 1, và nếu về 0 thì xoá luôn field, trong MỘT bước (script Lua chạy trọn vẹn bên trong Redis,
        /// không lệnh nào của ai chen vào giữa được).
        ///
        /// Nếu viết thành 2 lệnh riêng (HINCRBY -1, rồi HDEL nếu bằng 0) thì có khe hở: vừa trừ về 0, chưa kịp HDEL,
        /// một kết nối mới của cùng người đó cộng lên 1; rồi HDEL xoá mất field → người đang online bị coi là offline.
        /// </summary>
        private const string DecrementScript = """
            local count = redis.call('HINCRBY', KEYS[1], ARGV[1], -1)
            if count <= 0 then
                redis.call('HDEL', KEYS[1], ARGV[1])
            end
            return count
            """;

        private readonly IDatabase _redis;
        private readonly string _key;

        public PresenceService(IConnectionMultiplexer redis, IConfiguration configuration)
        {
            _redis = redis.GetDatabase();
            // Test dùng key riêng để không đụng danh sách online thật (giống Leaderboard:RedisKey).
            _key = configuration["Presence:RedisKey"] ?? "presence:online";
        }

        /// <summary>Thêm một kết nối của người chơi. Trả về true nếu đây là kết nối ĐẦU TIÊN (người đó vừa online).</summary>
        public async Task<bool> AddConnectionAsync(Guid playerId)
        {
            // HINCRBY presence:online {playerId} 1 → trả về giá trị SAU khi cộng
            long count = await _redis.HashIncrementAsync(_key, playerId.ToString(), 1);
            return count == 1;
        }

        /// <summary>Bớt một kết nối của người chơi. Trả về true nếu đó là kết nối CUỐI CÙNG (người đó vừa offline).</summary>
        public async Task<bool> RemoveConnectionAsync(Guid playerId)
        {
            RedisResult result = await _redis.ScriptEvaluateAsync(
                DecrementScript,
                keys: new RedisKey[] { _key },
                values: new RedisValue[] { playerId.ToString() });
            return (long)result <= 0;
        }

        /// <summary>Danh sách playerId đang online. Redis: HKEYS presence:online</summary>
        public async Task<List<Guid>> GetOnlinePlayerIdsAsync()
        {
            RedisValue[] fields = await _redis.HashKeysAsync(_key);
            return fields.Select(field => Guid.Parse(field.ToString())).ToList();
        }
    }
}
