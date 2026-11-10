namespace ServerSampleApi.Models
{
    /// <summary>Một clan (bảng "Clans").</summary>
    public class ClanModel
    {
        public Guid Id { get; set; }

        /// <summary>Tên clan, không trùng (không phân biệt hoa thường, cột citext).</summary>
        public string Name { get; set; } = "";

        /// <summary>Người chơi đang là chủ clan. Luôn là một thành viên của clan.</summary>
        public Guid LeaderId { get; set; }

        /// <summary>
        /// Số thành viên, lưu sẵn thay vì đếm bảng ClanMembers mỗi lần. Lý do chính: để kiểm tra "clan đã đầy chưa"
        /// NGAY TRONG câu UPDATE (giống nhận thưởng hằng ngày), hai người cùng vào chỗ trống cuối cùng thì chỉ một người được.
        /// </summary>
        public int MemberCount { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Một người chơi thuộc một clan (bảng "ClanMembers").
    /// Khoá chính là PlayerId: mỗi người chơi có TỐI ĐA MỘT dòng, tức chỉ ở trong một clan. Database tự đảm bảo
    /// luật này, kể cả khi một người gửi 2 request vào 2 clan khác nhau cùng lúc.
    /// </summary>
    public class ClanMemberModel
    {
        public Guid PlayerId { get; set; }

        public Guid ClanId { get; set; }

        /// <summary>Thời điểm vào clan. Chủ clan rời đi thì người vào sớm nhất được làm chủ.</summary>
        public DateTime JoinedAt { get; set; }

        /// <summary>Người chơi của dòng này (navigation property): dùng để đọc tên hiển thị trong cùng một câu SQL.</summary>
        public PlayerModel Player { get; set; } = null!;
    }
}
