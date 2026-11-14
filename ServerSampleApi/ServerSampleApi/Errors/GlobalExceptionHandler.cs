using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using StackExchange.Redis;

namespace ServerSampleApi.Errors
{
    /// <summary>
    /// Bắt mọi exception KHÔNG ai xử lý trong lúc chạy một request HTTP (lỗi BẤT NGỜ: database sập, bug trong code...).
    ///
    /// Khác lỗi nghiệp vụ (ServiceError: clan đầy, tên trùng...): những lỗi đó là kết quả bình thường, service trả về,
    /// controller chuyển thành 404/409. Còn ở đây là SỰ CỐ, nên:
    ///  - ghi log mức Error kèm toàn bộ exception (stack trace) và traceId, để lập trình viên tra lỗi;
    ///  - trả client một ProblemDetails gọn gàng, CÓ traceId nhưng KHÔNG có chi tiết bên trong (tên bảng, câu SQL,
    ///    địa chỉ database, stack trace...): những thứ đó giúp kẻ tấn công, còn người chơi thì không cần.
    ///
    /// traceId là "mã vụ việc": người chơi báo lỗi kèm mã này, lập trình viên tìm đúng dòng log có cùng mã.
    /// Được gọi bởi app.UseExceptionHandler() trong Program.cs.
    /// </summary>
    public sealed class GlobalExceptionHandler : IExceptionHandler
    {
        private readonly IProblemDetailsService _problemDetails;
        private readonly IHostEnvironment _environment;
        private readonly ILogger<GlobalExceptionHandler> _logger;

        public GlobalExceptionHandler(
            IProblemDetailsService problemDetails,
            IHostEnvironment environment,
            ILogger<GlobalExceptionHandler> logger)
        {
            _problemDetails = problemDetails;
            _environment = environment;
            _logger = logger;
        }

        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            {
                // Client đóng kết nối giữa chừng (tắt game, hủy request): hệ điều hành của client gửi gói TCP FIN/RST,
                // Kestrel thấy và hủy RequestAborted. Không phải lỗi của server, cũng không còn ai để trả lời.
                // Lưu ý:
                //  - Mất mạng ĐỘT NGỘT (rút dây, mất wifi) không gửi được gói nào, nên server không biết ngay.
                //  - Nhánh này chỉ chạy khi code có truyền CancellationToken (= RequestAborted) xuống EF/Redis...;
                //    nếu không, việc vẫn chạy hết và không có OperationCanceledException nào được ném ra.
                _logger.LogInformation("Request {Method} {Path} was cancelled by the client",
                    httpContext.Request.Method, httpContext.Request.Path);
                return true;
            }

            // Cùng một mã với "traceId" trong ProblemDetails trả về cho client.
            string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

            // Database / Redis không trả lời: 503 "tạm thời không phục vụ được", client có thể thử lại sau.
            // Mọi lỗi khác: 500 "lỗi bên trong server" (thường là bug), thử lại cũng vô ích.
            bool unavailable = IsDependencyUnavailable(exception);
            int statusCode = unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;

            _logger.LogError(exception, "Unhandled exception on {Method} {Path}, traceId {TraceId}",
                httpContext.Request.Method, httpContext.Request.Path, traceId);

            httpContext.Response.StatusCode = statusCode;
            return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = unavailable ? "Service temporarily unavailable" : "An unexpected error occurred",
                    // Development (máy bạn): kèm thông báo của exception cho dễ sửa lỗi.
                    // Môi trường khác (server thật): không bao giờ để lộ chi tiết bên trong.
                    Detail = _environment.IsDevelopment()
                        ? exception.Message
                        : unavailable ? "Please try again in a moment." : null,
                },
                // traceId được ProblemDetailsService tự thêm vào (giống các lỗi 400/404/409 do [ApiController] tạo).
            });
        }

        /// <summary>
        /// Lỗi do database hoặc Redis tạm thời không dùng được (không kết nối được, quá thời gian chờ...).
        /// Exception thường bị bọc nhiều lớp (EF Core bọc lỗi của Npgsql), nên xét cả các InnerException.
        /// </summary>
        private static bool IsDependencyUnavailable(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                // IsTransient: lỗi "tạm thời" (mất kết nối, timeout...), khác lỗi do câu SQL sai.
                if (current is NpgsqlException { IsTransient: true }
                    or RedisConnectionException
                    or RedisTimeoutException
                    or TimeoutException)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
