namespace ServerSampleApi.Models
{
    /// <summary>
    /// Dữ liệu tài khoản lưu trên server.
    /// KHÔNG bao giờ trả thẳng class này về cho client, vì nó chứa PasswordHash.
    /// </summary>
    public class AccountModel
    {
        public Guid Id { get; set; }

        public string Username { get; set; } = "";

        /// <summary>Không lưu mật khẩu gốc, chỉ lưu bản băm (hash).</summary>
        public string PasswordHash { get; set; } = "";

        public DateTime CreatedAt { get; set; }
    }
}
