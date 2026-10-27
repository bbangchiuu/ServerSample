using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Dữ liệu client gửi lên khi đăng ký (DTO = Data Transfer Object: class chỉ để chuyển dữ liệu qua API).
    /// Các attribute [Required], [StringLength]... được [ApiController] tự kiểm tra trước khi gọi hàm trong Controller.
    /// </summary>
    public class RegisterRequest
    {
        [Required]
        [StringLength(20, MinimumLength = 3)]
        [RegularExpression("^[a-zA-Z0-9_]+$", ErrorMessage = "Username may only contain letters, digits and '_'.")]
        public string Username { get; set; } = "";

        [Required]
        [StringLength(64, MinimumLength = 6)]
        public string Password { get; set; } = "";
    }
}
