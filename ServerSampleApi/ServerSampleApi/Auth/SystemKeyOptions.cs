using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace ServerSampleApi.Auth
{
    /// <summary>
    /// Khoá hệ thống (API key) cho các API quản trị, ví dụ dựng lại bảng xếp hạng. Đọc từ nhóm "System".
    ///
    /// Khác JWT ở chỗ: JWT trả lời "người chơi NÀO đang gọi", còn key này chỉ trả lời "người gọi có biết
    /// mật khẩu chung của hệ thống không". Hợp với tool quản trị, script chạy định kỳ, server khác gọi sang...
    /// Không hợp để phân biệt từng người: ai có key cũng như nhau, và lộ key là phải đổi cho TẤT CẢ.
    ///
    /// ApiKey là BÍ MẬT giống Jwt:SigningKey: khi dev nằm trong User Secrets
    /// (dotnet user-secrets set "System:ApiKey" "..."), khi chạy thật thì đặt biến môi trường System__ApiKey.
    /// </summary>
    public class SystemKeyOptions
    {
        public const string SectionName = "System";

        [Required]
        [MinLength(32)]
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// So key client gửi lên với key thật.
        /// Dùng FixedTimeEquals thay vì ==: phép == dừng ngay ở ký tự sai ĐẦU TIÊN, nên đoán đúng càng nhiều ký tự đầu
        /// thì server trả lời càng chậm một chút. Kẻ xấu đo thời gian đó để dò ra key từng ký tự một (timing attack).
        /// FixedTimeEquals luôn so HẾT, đúng hay sai đều tốn cùng một thời gian.
        /// </summary>
        public bool IsValid(string? sentKey)
        {
            if (string.IsNullOrEmpty(sentKey))
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sentKey), Encoding.UTF8.GetBytes(ApiKey));
        }
    }
}
