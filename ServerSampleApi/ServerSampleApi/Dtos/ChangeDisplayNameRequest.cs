using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Dtos
{
    /// <summary>Dữ liệu client gửi lên khi đổi tên hiển thị.</summary>
    public class ChangeDisplayNameRequest
    {
        [Required]
        [StringLength(20, MinimumLength = 3)]
        public string DisplayName { get; set; } = "";
    }
}
