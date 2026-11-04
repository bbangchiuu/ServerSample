namespace ServerSampleApi.Models
{
    /// <summary>
    /// Một refresh token đã phát cho người chơi (bảng "RefreshTokens"). Mỗi lần đăng nhập / làm mới token là một dòng.
    ///
    /// Khác access token (JWT): server KHÔNG lưu JWT, nên không thu hồi được JWT. Còn refresh token thì server lưu,
    /// nên thu hồi được bất cứ lúc nào (đăng xuất, nghi bị lộ...) bằng cách điền RevokedAt.
    /// </summary>
    public class RefreshTokenModel
    {
        public Guid Id { get; set; }

        /// <summary>Token này của tài khoản nào.</summary>
        public Guid AccountId { get; set; }

        /// <summary>
        /// Bản băm SHA-256 của token, KHÔNG lưu token gốc (giống mật khẩu): lỡ lộ database thì kẻ xấu
        /// cũng không có token để dùng. Client gửi token gốc lên, server băm lại rồi tìm theo bản băm.
        /// </summary>
        public string TokenHash { get; set; } = "";

        public DateTime CreatedAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        /// <summary>Thời điểm bị thu hồi (đã dùng để làm mới, hoặc đã đăng xuất). null = còn dùng được.</summary>
        public DateTime? RevokedAt { get; set; }

        /// <summary>
        /// Token đã được dùng để làm mới thì đây là Id của token mới thay thế nó. null nếu chưa dùng, hoặc bị thu hồi
        /// vì đăng xuất. Nhờ cột này mà phân biệt được: token ĐÃ DÙNG bị gửi lại (đáng ngờ, có thể bị đánh cắp)
        /// với token ĐÃ ĐĂNG XUẤT bị gửi lại (bình thường, chỉ cần từ chối).
        /// </summary>
        public Guid? ReplacedByTokenId { get; set; }
    }
}
