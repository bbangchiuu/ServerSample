using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Auth
{
    /// <summary>
    /// Giới hạn số lần gọi các API tài khoản, đọc từ nhóm "RateLimiting".
    ///
    /// Không có giới hạn thì kẻ xấu gửi hàng nghìn request /login mỗi giây để dò mật khẩu (brute force),
    /// hoặc gọi /register liên tục để tạo hàng triệu tài khoản rác.
    ///
    /// Đếm theo IP: mỗi IP có một "hạn mức" riêng trong mỗi khoảng thời gian (cửa sổ). Hết hạn mức thì server
    /// trả 429 Too Many Requests, kèm header Retry-After cho biết bao nhiêu giây nữa thì thử lại được.
    /// Đếm CẢ lần đúng lẫn lần sai: server không biết trước request nào là người thật nhập sai, request nào là kẻ dò.
    /// </summary>
    public class RateLimitingOptions
    {
        public const string SectionName = "RateLimiting";

        /// <summary>Tên policy, dùng trong [EnableRateLimiting(RateLimitingOptions.LoginPolicy)].</summary>
        public const string LoginPolicy = "login";

        public const string RegisterPolicy = "register";

        /// <summary>Mỗi IP được thử đăng nhập tối đa bao nhiêu lần trong một cửa sổ.</summary>
        [Range(1, 10_000)]
        public int LoginPermitLimit { get; set; } = 5;

        [Range(1, 86_400)]
        public int LoginWindowSeconds { get; set; } = 60;

        /// <summary>Mỗi IP được tạo tối đa bao nhiêu tài khoản trong một cửa sổ.</summary>
        [Range(1, 10_000)]
        public int RegisterPermitLimit { get; set; } = 10;

        [Range(1, 86_400)]
        public int RegisterWindowSeconds { get; set; } = 3600;
    }
}
