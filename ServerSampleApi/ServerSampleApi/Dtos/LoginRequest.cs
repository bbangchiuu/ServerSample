using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Dữ liệu client gửi lên khi đăng nhập.
    /// Chỉ cần [Required], không kiểm tra độ dài như lúc đăng ký: sai độ dài thì đằng nào cũng
    /// không khớp tài khoản nào, và không nên tiết lộ luật đặt mật khẩu ở màn hình đăng nhập.
    /// </summary>
    public class LoginRequest
    {
        [Required]
        public string Username { get; set; } = "";

        [Required]
        public string Password { get; set; } = "";
    }
}
