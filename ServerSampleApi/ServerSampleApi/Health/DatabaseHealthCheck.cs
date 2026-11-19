using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServerSampleApi.Data;

namespace ServerSampleApi.Health
{
    /// <summary>
    /// Kiểm tra PostgreSQL: mở được kết nối tới database không.
    ///
    /// IHealthCheck chỉ có MỘT hàm: CheckHealthAsync, trả về Healthy / Degraded / Unhealthy.
    /// Mỗi lần có ai gọi /health/ready, ASP.NET Core tạo một DI scope mới rồi tạo class này trong scope đó,
    /// nên nhận GameDbContext (Scoped) qua constructor được, giống một request bình thường.
    /// </summary>
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly GameDbContext _db;

        public DatabaseHealthCheck(GameDbContext db)
        {
            _db = db;
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            // CanConnectAsync: thử mở kết nối, trả true/false (không ném exception khi database tắt).
            bool canConnect = await _db.Database.CanConnectAsync(cancellationToken);

            // context.Registration.FailureStatus: trạng thái khi lỗi, do Program.cs quyết định lúc đăng ký
            // (với database là Unhealthy). Mô tả chỉ là câu ngắn, KHÔNG kèm chi tiết lỗi (địa chỉ, mật khẩu...):
            // /health/ready không cần đăng nhập, ai cũng gọi được.
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : new HealthCheckResult(context.Registration.FailureStatus, "Cannot connect to PostgreSQL.");
        }
    }
}
