using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ServerSampleApi.Health
{
    /// <summary>
    /// Health check: hai địa chỉ để Docker / load balancer hỏi "server còn ổn không?" (xem Program.cs).
    ///  - /health/live:  "process còn sống không?". KHÔNG kiểm tra gì bên ngoài: trả lời được là sống.
    ///                   Không trả lời được (treo, chết) thì Docker nên KHỞI ĐỘNG LẠI container.
    ///  - /health/ready: "có sẵn sàng nhận người chơi không?". Kiểm tra PostgreSQL và Redis.
    ///                   503 thì load balancer TẠM NGỪNG gửi người chơi vào server này (không khởi động lại).
    ///
    /// Vì sao live KHÔNG kiểm tra database: database sập thì khởi động lại server cũng vô ích (database vẫn sập),
    /// mà còn làm mọi server khởi động lại liên tục. Đó là việc của ready, không phải của live.
    /// </summary>
    public static class HealthCheckSetup
    {
        public const string LivePath = "/health/live";
        public const string ReadyPath = "/health/ready";

        /// <summary>Tag "ready": chỉ các check có tag này mới chạy ở /health/ready.</summary>
        private const string ReadyTag = "ready";

        // Mỗi check tối đa 3 giây: health check phải trả lời NHANH, Docker / load balancer không chờ lâu.
        private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

        public static IServiceCollection AddGameHealthChecks(this IServiceCollection services)
        {
            services.AddHealthChecks()
                // PostgreSQL lỗi: Unhealthy (503). Không có database thì không đăng nhập được, server vô dụng.
                .AddCheck<DatabaseHealthCheck>("postgres", HealthStatus.Unhealthy, [ReadyTag], CheckTimeout)
                // Redis lỗi: Degraded (VẪN 200). Phần chính vẫn chạy được; báo 503 thì load balancer gỡ MỌI server
                // ra cùng lúc (tất cả dùng chung một Redis), biến lỗi nhỏ thành sập hoàn toàn.
                .AddCheck<RedisHealthCheck>("redis", HealthStatus.Degraded, [ReadyTag], CheckTimeout);
            return services;
        }

        public static IEndpointRouteBuilder MapGameHealthChecks(this IEndpointRouteBuilder endpoints)
        {
            // Predicate = _ => false: không chạy check nào, chỉ cần trả lời được là "Healthy".
            endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
            {
                Predicate = _ => false,
                ResponseWriter = WriteJsonAsync,
            });

            // Chỉ chạy các check có tag "ready". Mặc định: Healthy và Degraded → 200, Unhealthy → 503.
            endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
            {
                Predicate = check => check.Tags.Contains(ReadyTag),
                ResponseWriter = WriteJsonAsync,
            });

            return endpoints;
        }

        /// <summary>
        /// Mặc định health check chỉ trả chữ "Healthy". Ở đây trả JSON có trạng thái từng phần, để người vận hành
        /// nhìn là biết PHẦN NÀO hỏng, ví dụ:
        ///   {"status":"Degraded","durationMs":12.3,"checks":{"postgres":{"status":"Healthy",...},"redis":{"status":"Degraded",...}}}
        /// Chỉ có mô tả ngắn, KHÔNG có nội dung exception: endpoint này không cần đăng nhập.
        /// </summary>
        private static Task WriteJsonAsync(HttpContext context, HealthReport report)
        {
            return context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new
                    {
                        status = entry.Value.Status.ToString(),
                        durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                        description = entry.Value.Description,
                    }),
            });
        }
    }
}
