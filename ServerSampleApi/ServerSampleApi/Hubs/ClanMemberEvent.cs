namespace ServerSampleApi.Hubs
{
    /// <summary>
    /// Một người vừa vào hoặc rời clan (IGameClient.ClanMemberJoined / ClanMemberLeft).
    /// Kèm luôn chủ clan và số thành viên SAU thay đổi, để client cập nhật giao diện mà không phải gọi lại API.
    /// </summary>
    public class ClanMemberEvent
    {
        public Guid ClanId { get; set; }

        /// <summary>Người vừa vào / vừa rời.</summary>
        public Guid PlayerId { get; set; }

        public string DisplayName { get; set; } = "";

        /// <summary>Chủ clan sau thay đổi (có thể đã đổi nếu chủ cũ vừa rời). null = clan đã bị xoá vì hết người.</summary>
        public Guid? LeaderId { get; set; }

        /// <summary>Số thành viên sau thay đổi.</summary>
        public int MemberCount { get; set; }
    }
}
