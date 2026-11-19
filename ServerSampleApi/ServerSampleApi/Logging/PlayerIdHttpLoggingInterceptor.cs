using System.Security.Claims;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ServerSampleApi.Logging
{
    /// <summary>
    /// Thêm "PlayerId" vào dòng log của mỗi request (do app.UseHttpLogging() ghi, xem Program.cs).
    ///
    /// HttpLogging có sẵn ghi được method, path, status, thời gian xử lý... nhưng không biết "người chơi nào".
    /// Interceptor là chỗ để chen thêm thông tin riêng của mình vào dòng log đó.
    ///
    /// Thêm ở OnResponseAsync (lúc bắt đầu trả lời) chứ không ở OnRequestAsync: lúc request mới tới,
    /// middleware Authentication (nằm SAU UseHttpLogging trong pipeline) chưa chạy, nên chưa biết token là của ai.
    /// </summary>
    public sealed class PlayerIdHttpLoggingInterceptor : IHttpLoggingInterceptor
    {
        public ValueTask OnRequestAsync(HttpLoggingInterceptorContext logContext) => ValueTask.CompletedTask;

        public ValueTask OnResponseAsync(HttpLoggingInterceptorContext logContext)
        {
            // Không có token hợp lệ (đăng nhập, đăng ký, 401...) thì không có "sub": bỏ qua, không ghi gì thêm.
            string? playerId = logContext.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (playerId != null)
            {
                logContext.AddParameter("PlayerId", playerId);
            }
            return ValueTask.CompletedTask;
        }
    }
}
