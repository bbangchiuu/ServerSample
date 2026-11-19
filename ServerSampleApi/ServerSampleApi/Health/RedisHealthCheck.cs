using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace ServerSampleApi.Health
{
    /// <summary>
    /// Kiểm tra Redis: gửi lệnh PING, Redis trả lời PONG là ổn.
    /// Khi lỗi trả về trạng thái do Program.cs đăng ký (Degraded, không phải Unhealthy): Redis tắt thì server vẫn
    /// phục vụ được phần chính (đăng nhập, profile, nhận thưởng), chỉ mất bảng xếp hạng, danh sách online, realtime.
    /// </summary>
    public sealed class RedisHealthCheck : IHealthCheck
    {
        private readonly IConnectionMultiplexer _redis;

        public RedisHealthCheck(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // PingAsync không nhận CancellationToken: WaitAsync để bỏ chờ khi hết thời gian của health check.
                TimeSpan latency = await _redis.GetDatabase().PingAsync().WaitAsync(cancellationToken);
                return HealthCheckResult.Healthy($"Redis answered PING in {latency.TotalMilliseconds:0} ms.");
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException or OperationCanceledException)
            {
                // Ghi chi tiết lỗi vào log của server (exception), còn mô tả trả ra ngoài chỉ là câu ngắn.
                return new HealthCheckResult(context.Registration.FailureStatus, "Cannot reach Redis.", ex);
            }
        }
    }
}
