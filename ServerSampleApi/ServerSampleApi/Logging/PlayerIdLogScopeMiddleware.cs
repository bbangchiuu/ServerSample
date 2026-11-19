using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ServerSampleApi.Logging
{
    /// <summary>
    /// Gắn "PlayerId" vào MỌI dòng log phát sinh trong một request (log của controller, service, GlobalExceptionHandler...),
    /// không chỉ dòng log tổng kết của HttpLogging.
    ///
    /// Cơ chế: logger.BeginScope(...) mở một "scope". Mọi log ghi ra trong lúc scope còn mở (kể cả ở class khác,
    /// kể cả sau await) đều mang theo dữ liệu của scope. TraceId cũng được ASP.NET Core gắn theo đúng cách này.
    /// Bộ định dạng JSON (IncludeScopes = true trong appsettings.json) in các scope ra cùng dòng log.
    ///
    /// Phải chạy SAU app.UseAuthentication(): trước đó chưa biết token là của ai.
    /// </summary>
    public static class PlayerIdLogScopeMiddleware
    {
        public static IApplicationBuilder UsePlayerIdLogScope(this IApplicationBuilder app)
        {
            ILogger logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("ServerSampleApi.Requests");

            return app.Use(async (context, next) =>
            {
                string? playerId = context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
                if (playerId == null)
                {
                    await next(context);
                    return;
                }

                // Scope dùng chung cho mọi logger: mở bằng logger nào thì log của class khác cũng nhận được.
                using (logger.BeginScope(new Dictionary<string, object> { ["PlayerId"] = playerId }))
                {
                    await next(context);
                }
            });
        }
    }
}
