using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace ServerSampleApi.Auth
{
    /// <summary>
    /// Gắn lên API (hoặc cả controller) để bắt buộc header "System-Authorization: &lt;System:ApiKey&gt;".
    /// Thiếu header hoặc sai key thì trả 401, KHÔNG chạy vào hàm của API.
    ///
    ///     [HttpPost("rebuild")]
    ///     [RequireSystemKey]
    ///     public async Task&lt;...&gt; Rebuild() { ... }
    ///
    /// IAuthorizationFilter: ASP.NET Core gọi OnAuthorization TRƯỚC khi đọc body, kiểm tra dữ liệu và chạy hàm API.
    /// Gán context.Result là "chặn lại": server trả luôn kết quả đó, bỏ qua phần còn lại.
    /// Không dùng header "Authorization" vì header đó đã dành cho JWT ("Bearer ...").
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RequireSystemKeyAttribute : Attribute, IAuthorizationFilter
    {
        public const string HeaderName = "System-Authorization";

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            // Attribute được tạo một lần lúc khởi động, không nhận service qua constructor được,
            // nên lấy cấu hình từ RequestServices (DI của request hiện tại).
            SystemKeyOptions options = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<SystemKeyOptions>>().Value;

            string? sentKey = context.HttpContext.Request.Headers[HeaderName];
            if (!options.IsValid(sentKey))
            {
                context.Result = new UnauthorizedObjectResult(new ProblemDetails
                {
                    Title = "Invalid system key",
                    Detail = $"Send the system key in the '{HeaderName}' header.",
                    Status = StatusCodes.Status401Unauthorized,
                });
            }
        }
    }
}
