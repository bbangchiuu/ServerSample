using StackExchange.Redis;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Ai đang online, lưu trong Redis (để nhiều server cùng thấy chung một danh sách).
    ///
    /// Ba key (prefix mặc định "presence"):
    ///   presence:players            Hash       playerId → TỔNG số kết nối đang mở, trên MỌI server
    ///   presence:server:{serverId}  Hash       playerId → số kết nối trên RIÊNG server đó ("phần" của server)
    ///   presence:servers            SortedSet  serverId → thời điểm server đó báo "còn sống" gần nhất (heartbeat, ms)
    ///
    /// Đếm số kết nối, không chỉ đánh dấu "có/không": Alice tắt điện thoại mà máy tính vẫn mở thì vẫn online.
    /// Tổng 0 → 1: vừa online. 1 → 0: vừa offline. Chỉ hai lúc đó mới báo cho mọi người.
    ///
    /// Vì sao mỗi server giữ "phần" riêng: server bị tắt đột ngột (crash, bấm Stop trong Visual Studio) thì
    /// OnDisconnectedAsync không kịp chạy, không ai trừ bộ đếm, người chơi bị "kẹt" online mãi. Có phần riêng thì
    /// server khác biết chính xác phải trừ bao nhiêu cho ai: server chết không còn báo "còn sống" nữa, quá
    /// ServerTimeout thì một server còn sống dọn phần của nó (RemoveDeadServersAsync).
    ///
    /// Mọi thay đổi gồm nhiều lệnh đều viết bằng script Lua: Redis chạy trọn một script, không lệnh nào của ai
    /// (kể cả server khác) chen vào giữa được, nên các bộ đếm luôn khớp nhau.
    ///
    /// Singleton: mỗi process server có MỘT ServerId, sinh ra lúc khởi động.
    /// </summary>
    public class PresenceService
    {
        /// <summary>Server cập nhật heartbeat mỗi khoảng này (PresenceHeartbeatService).</summary>
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

        /// <summary>Quá khoảng này không có heartbeat thì coi như server đã chết. Gấp 3 lần HeartbeatInterval cho chắc.</summary>
        public static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Thêm một kết nối. KEYS: [1] players, [2] servers, [3] phần của server này. ARGV: [1] playerId, [2] serverId, [3] now.
        /// Trả về TỔNG số kết nối của người chơi sau khi cộng.
        /// </summary>
        private const string AddConnectionScript = """
            redis.call('ZADD', KEYS[2], ARGV[3], ARGV[2])
            redis.call('HINCRBY', KEYS[3], ARGV[1], 1)
            return redis.call('HINCRBY', KEYS[1], ARGV[1], 1)
            """;

        /// <summary>
        /// Bớt một kết nối. KEYS: [1] players, [2] phần của server này. ARGV: [1] playerId.
        /// Trả về TỔNG số kết nối còn lại (0 = vừa offline), hoặc -1 nếu phần của server này không có người chơi đó:
        /// phần đã bị dọn trước rồi (server bị coi là chết, hoặc đang tắt), trừ nữa thì thành trừ hai lần.
        /// </summary>
        private const string RemoveConnectionScript = """
            if redis.call('HEXISTS', KEYS[2], ARGV[1]) == 0 then
                return -1
            end
            local own = redis.call('HINCRBY', KEYS[2], ARGV[1], -1)
            if own <= 0 then
                redis.call('HDEL', KEYS[2], ARGV[1])
            end
            local total = redis.call('HINCRBY', KEYS[1], ARGV[1], -1)
            if total <= 0 then
                redis.call('HDEL', KEYS[1], ARGV[1])
                total = 0
            end
            return total
            """;

        /// <summary>
        /// Dọn phần của MỘT server. KEYS: [1] players, [2] servers, [3] phần của server cần dọn. ARGV: [1] serverId đó.
        /// Trả về danh sách playerId vừa offline (tổng về 0).
        ///
        /// ZREM trả 1 nếu xoá được, 0 nếu server đó không còn trong danh sách: hai server còn sống cùng phát hiện một
        /// server chết thì chỉ một server "giành" được việc dọn, server kia nhận 0 và dừng, không trừ hai lần.
        /// HGETALL trả về mảng phẳng [playerId1, count1, playerId2, count2, ...], nên duyệt từng cặp (bước nhảy 2).
        /// </summary>
        private const string RemoveServerScript = """
            if redis.call('ZREM', KEYS[2], ARGV[1]) == 0 then
                return {}
            end
            local entries = redis.call('HGETALL', KEYS[3])
            redis.call('DEL', KEYS[3])
            local offline = {}
            for i = 1, #entries, 2 do
                local total = redis.call('HINCRBY', KEYS[1], entries[i], -tonumber(entries[i + 1]))
                if total <= 0 then
                    redis.call('HDEL', KEYS[1], entries[i])
                    table.insert(offline, entries[i])
                end
            end
            return offline
            """;

        private readonly IDatabase _redis;
        private readonly string _playersKey;
        private readonly string _serversKey;
        private readonly string _serverKeyPrefix;

        public PresenceService(IConnectionMultiplexer redis, IConfiguration configuration)
        {
            _redis = redis.GetDatabase();

            // Test dùng prefix riêng để không đụng danh sách online thật (giống Leaderboard:RedisKey).
            string prefix = configuration["Presence:RedisKeyPrefix"] ?? "presence";
            _playersKey = $"{prefix}:players";
            _serversKey = $"{prefix}:servers";
            _serverKeyPrefix = $"{prefix}:server:";
        }

        /// <summary>Mã của process server này. Khởi động lại là một mã mới (phần cũ được dọn như một server đã chết).</summary>
        public string ServerId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>Thêm một kết nối của người chơi vào server này. Trả về true nếu đây là kết nối ĐẦU TIÊN (vừa online).</summary>
        public async Task<bool> AddConnectionAsync(Guid playerId)
        {
            RedisResult total = await _redis.ScriptEvaluateAsync(AddConnectionScript,
                keys: new RedisKey[] { _playersKey, _serversKey, ServerKey(ServerId) },
                values: new RedisValue[] { playerId.ToString(), ServerId, NowMilliseconds() });
            return (long)total == 1;
        }

        /// <summary>Bớt một kết nối của người chơi trên server này. Trả về true nếu đó là kết nối CUỐI CÙNG (vừa offline).</summary>
        public async Task<bool> RemoveConnectionAsync(Guid playerId)
        {
            RedisResult total = await _redis.ScriptEvaluateAsync(RemoveConnectionScript,
                keys: new RedisKey[] { _playersKey, ServerKey(ServerId) },
                values: new RedisValue[] { playerId.ToString() });
            return (long)total == 0;
        }

        /// <summary>Danh sách playerId đang online. Redis: HKEYS presence:players</summary>
        public async Task<List<Guid>> GetOnlinePlayerIdsAsync()
        {
            RedisValue[] fields = await _redis.HashKeysAsync(_playersKey);
            return fields.Select(field => Guid.Parse(field.ToString())).ToList();
        }

        /// <summary>Báo "server này còn sống". Redis: ZADD presence:servers {now} {serverId}</summary>
        public Task HeartbeatAsync()
        {
            return _redis.SortedSetAddAsync(_serversKey, ServerId, NowMilliseconds());
        }

        /// <summary>
        /// Tìm các server quá ServerTimeout không có heartbeat, dọn phần của chúng.
        /// Trả về playerId của những người vừa offline vì vậy (để báo PlayerOffline).
        /// </summary>
        public async Task<List<Guid>> RemoveDeadServersAsync()
        {
            // ZRANGEBYSCORE presence:servers -inf {now - 30s}: các server có heartbeat cuối cùng quá 30 giây trước.
            double deadline = NowMilliseconds() - ServerTimeout.TotalMilliseconds;
            RedisValue[] deadServers = await _redis.SortedSetRangeByScoreAsync(_serversKey, double.NegativeInfinity, deadline);

            var offline = new List<Guid>();
            foreach (RedisValue serverId in deadServers)
            {
                if (serverId != ServerId) // phòng trường hợp chính server này bị chậm: không tự dọn mình ở đây
                {
                    offline.AddRange(await RemoveServerAsync(serverId.ToString()));
                }
            }
            return offline;
        }

        /// <summary>Dọn phần của chính server này (gọi khi server tắt bình thường). Trả về những người vừa offline.</summary>
        public Task<List<Guid>> RemoveThisServerAsync()
        {
            return RemoveServerAsync(ServerId);
        }

        private async Task<List<Guid>> RemoveServerAsync(string serverId)
        {
            RedisResult result = await _redis.ScriptEvaluateAsync(RemoveServerScript,
                keys: new RedisKey[] { _playersKey, _serversKey, ServerKey(serverId) },
                values: new RedisValue[] { serverId });

            // Script trả về một mảng Lua → RedisResult[] trong C#.
            return ((RedisResult[])result!).Select(playerId => Guid.Parse(playerId.ToString())).ToList();
        }

        private string ServerKey(string serverId) => _serverKeyPrefix + serverId;

        private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}
