using System.ComponentModel.DataAnnotations;

namespace ServerSampleApi.Dtos
{
    /// <summary>Dữ liệu client gửi lên khi tạo clan.</summary>
    public class CreateClanRequest
    {
        [Required]
        [StringLength(20, MinimumLength = 3)]
        public string Name { get; set; } = "";
    }

    /// <summary>Thông tin đầy đủ của một clan, kèm danh sách thành viên.</summary>
    public class ClanResponse
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public Guid LeaderId { get; set; }

        public int MemberCount { get; set; }

        /// <summary>Số thành viên tối đa, gửi kèm để client hiển thị "12/30" mà không phải tự biết luật của server.</summary>
        public int MaxMembers { get; set; }

        public DateTime CreatedAt { get; set; }

        /// <summary>Thành viên, ai vào trước đứng trước.</summary>
        public List<ClanMemberResponse> Members { get; set; } = new();
    }

    public class ClanMemberResponse
    {
        public Guid PlayerId { get; set; }

        public string DisplayName { get; set; } = "";

        public bool IsLeader { get; set; }

        public DateTime JoinedAt { get; set; }
    }

    /// <summary>Kết quả tìm clan. Bọc danh sách trong một object vì JsonUtility không đọc được JSON bắt đầu bằng mảng.</summary>
    public class ClanListResponse
    {
        public List<ClanSummaryResponse> Clans { get; set; } = new();
    }

    /// <summary>Thông tin ngắn của một clan trong danh sách tìm kiếm (không kèm thành viên).</summary>
    public class ClanSummaryResponse
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public int MemberCount { get; set; }

        public int MaxMembers { get; set; }
    }
}
