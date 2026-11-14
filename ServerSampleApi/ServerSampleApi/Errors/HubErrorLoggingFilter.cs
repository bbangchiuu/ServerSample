using Microsoft.AspNetCore.SignalR;

namespace ServerSampleApi.Errors
{
    /// <summary>
    /// Bộ lọc của SignalR: chạy "bao quanh" MỌI lần client gọi một hàm hub (SendChatMessage, GetOnlinePlayers...),
    /// giống middleware của HTTP. Hub không đi qua app.UseExceptionHandler(), nên lỗi bất ngờ trong hub cần chỗ bắt riêng.
    ///
    /// Chỉ GHI LOG rồi ném tiếp (throw;), không đổi gì ở phía client:
    ///  - HubException (lỗi nghiệp vụ, ví dụ "You are not in a clan."): bỏ qua, client vẫn nhận nguyên văn.
    ///  - Lỗi khác (database sập, bug...): ghi log mức Error kèm tên hàm và người chơi. Client vẫn chỉ nhận thông báo
    ///    chung chung của SignalR, không lộ chi tiết bên trong.
    /// Đăng ký trong Program.cs: AddSignalR(options => options.AddFilter&lt;HubErrorLoggingFilter&gt;()).
    /// </summary>
    public sealed class HubErrorLoggingFilter : IHubFilter
    {
        private readonly ILogger<HubErrorLoggingFilter> _logger;

        public HubErrorLoggingFilter(ILogger<HubErrorLoggingFilter> logger)
        {
            _logger = logger;
        }

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next)
        {
            try
            {
                // Chạy hàm hub thật (hoặc bộ lọc tiếp theo, nếu có nhiều bộ lọc).
                return await next(invocationContext);
            }
            catch (Exception ex) when (ex is not HubException)
            {
                _logger.LogError(ex, "Hub method {HubMethod} failed for player {PlayerId} (connection {ConnectionId})",
                    invocationContext.HubMethodName,
                    invocationContext.Context.UserIdentifier,
                    invocationContext.Context.ConnectionId);

                // "throw;" giữ nguyên exception và stack trace gốc, để SignalR xử lý tiếp như bình thường.
                throw;
            }
        }
    }
}
