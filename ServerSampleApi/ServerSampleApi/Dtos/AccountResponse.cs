namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Dữ liệu trả về cho client sau khi đăng ký. Chỉ chứa những gì client cần biết,
    /// tách khỏi Account để không bao giờ lộ PasswordHash ra ngoài.
    /// </summary>
    public class AccountResponse
    {
        public Guid PlayerId { get; set; }

        public string Username { get; set; } = "";
    }
}
