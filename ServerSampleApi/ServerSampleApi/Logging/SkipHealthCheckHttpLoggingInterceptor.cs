using Microsoft.AspNetCore.HttpLogging;

namespace ServerSampleApi.Logging
{
    /// <summary>
    /// KHÔNG ghi dòng log HttpLogging cho các request health check.
    ///
    /// Docker / load balancer gọi /health/... vài giây một lần, suốt ngày đêm: ghi hết thì log toàn dòng health check,
    /// che mất log của người chơi thật. Health check thất bại vẫn có log riêng (HealthCheckService tự ghi
    /// cảnh báo khi một check bị Degraded / Unhealthy), nên bỏ dòng HttpLogging không mất thông tin gì quan trọng.
    ///
    /// Làm ở OnRequestAsync (lúc request vừa tới): quyết định "request này có ghi log không" càng sớm càng tốt.
    /// </summary>
    public sealed class SkipHealthCheckHttpLoggingInterceptor : IHttpLoggingInterceptor
    {
        public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext)
        {
            if (logContext.HttpContext.Request.Path.StartsWithSegments("/health"))
            {
                // None: tắt MỌI trường của request này, HttpLogging không ghi dòng nào.
                logContext.LoggingFields = HttpLoggingFields.None;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext) => ValueTask.CompletedTask;
    }
}
