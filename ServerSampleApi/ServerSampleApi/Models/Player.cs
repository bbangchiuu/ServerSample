namespace ServerSampleApi.Models
{
    /// <summary>
    /// Dữ liệu game của một người chơi. Dùng chung Id với Account: 1 tài khoản = 1 nhân vật.
    /// Tách riêng khỏi Account: thông tin đăng nhập (bảo mật) không nằm chung với dữ liệu game.
    /// </summary>
    public class Player
    {
        public Guid Id { get; set; }

        public string DisplayName { get; set; } = "";

        public int Level { get; set; } = 1;

        public long Gold { get; set; }
    }
}
