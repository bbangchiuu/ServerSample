using System;
using System.Collections.Generic;

// Các class dữ liệu đi qua HTTP API, tương ứng với các DTO trong ServerSampleApi/Dtos.
//
// Luật của JsonUtility (bộ đọc/ghi JSON có sẵn của Unity):
//  - Class phải có [Serializable].
//  - Chỉ đọc/ghi FIELD public, KHÔNG đọc property { get; set; }.
//  - Tên field phải trùng tên trong JSON. Server trả về kiểu camelCase ("playerId"),
//    nên field ở đây cũng viết "playerId" chứ không phải "PlayerId".
//  - Không đọc được Dictionary, Guid, DateOnly: Guid và ngày tháng được đọc dưới dạng string.

[Serializable]
public class AccountRequest
{
    public string username;
    public string password;
}

[Serializable]
public class AccountResponse
{
    public string playerId;
    public string username;

    /// <summary>JWT. ApiClient tự giữ lại và gửi kèm các API cần đăng nhập, bạn không cần tự xử lý.</summary>
    public string accessToken;

    /// <summary>Thời điểm token hết hạn (UTC), ví dụ "2026-11-03T10:00:00Z". Đọc dạng string vì JsonUtility không đọc được DateTime.</summary>
    public string accessTokenExpiresAt;

    /// <summary>Dùng để lấy access token mới khi access token hết hạn. ApiClient tự giữ và tự dùng.</summary>
    public string refreshToken;

    /// <summary>Thời điểm refresh token hết hạn (UTC). Quá thời điểm này thì phải đăng nhập lại bằng mật khẩu.</summary>
    public string refreshTokenExpiresAt;
}

/// <summary>Gửi lên /api/accounts/refresh và /api/accounts/logout.</summary>
[Serializable]
public class RefreshTokenRequest
{
    public string refreshToken;
}

/// <summary>Dùng cho các API không trả về dữ liệu gì (204 No Content), ví dụ /api/accounts/logout.</summary>
[Serializable]
public class EmptyResponse
{
}

[Serializable]
public class ChangeDisplayNameRequest
{
    public string displayName;
}

[Serializable]
public class PlayerProfile
{
    public string id;
    public string displayName;
    public int level;
    public long gold;

    /// <summary>Ví dụ "2026-10-29". Rỗng nếu chưa nhận thưởng lần nào.</summary>
    public string lastDailyRewardDate;
}

[Serializable]
public class InventoryResponse
{
    public List<InventoryItem> items;
}

[Serializable]
public class InventoryItem
{
    public string itemId;
    public int quantity;
}

/// <summary>Một dòng trên bảng xếp hạng. Server đẩy xuống qua hub (hàm "LeaderboardChanged") khi có người đổi số vàng.</summary>
[Serializable]
public class LeaderboardEntry
{
    public long rank;
    public string playerId;
    public string displayName;
    public long gold;
}

/// <summary>Một người chơi đang online. Server đẩy xuống qua hub ("PlayerOnline", "PlayerOffline").</summary>
[Serializable]
public class OnlinePlayer
{
    public string playerId;
    public string displayName;
}

/// <summary>Kết quả của hàm hub "GetOnlinePlayers": {"players":[...]}.</summary>
[Serializable]
public class OnlinePlayersResponse
{
    public List<OnlinePlayer> players;
}

/// <summary>Tin nhắn chat, server đẩy xuống qua hub (hàm "ReceiveChatMessage"). Giống Hubs/ChatMessage.cs ở server.</summary>
[Serializable]
public class ChatMessage
{
    public string playerId;
    public string displayName;
    public string text;

    /// <summary>Thời điểm server nhận tin (UTC), ví dụ "2026-11-07T08:48:03.123Z".</summary>
    public string sentAt;
}

// ===== Clan =====

[Serializable]
public class CreateClanRequest
{
    public string name;
}

/// <summary>Một clan kèm danh sách thành viên (GET /api/clans/{id}, /api/clans/me, kết quả tạo / vào clan).</summary>
[Serializable]
public class ClanResponse
{
    public string id;
    public string name;
    public string leaderId;
    public int memberCount;
    public int maxMembers;
    public string createdAt;
    public List<ClanMember> members;
}

[Serializable]
public class ClanMember
{
    public string playerId;
    public string displayName;
    public bool isLeader;
    public string joinedAt;
}

/// <summary>Kết quả tìm clan: {"clans":[...]}.</summary>
[Serializable]
public class ClanListResponse
{
    public List<ClanSummary> clans;
}

[Serializable]
public class ClanSummary
{
    public string id;
    public string name;
    public int memberCount;
    public int maxMembers;
}

/// <summary>
/// Có người vào / rời clan. Server đẩy xuống qua hub ("ClanMemberJoined", "ClanMemberLeft").
/// leaderId và memberCount là giá trị SAU thay đổi. leaderId null/rỗng = clan đã bị xoá (người cuối cùng vừa rời).
/// </summary>
[Serializable]
public class ClanMemberEvent
{
    public string clanId;
    public string playerId;
    public string displayName;
    public string leaderId;
    public int memberCount;
}

/// <summary>Định dạng lỗi chuẩn mà server trả về cho mọi lỗi 4xx/5xx.</summary>
[Serializable]
public class ProblemDetails
{
    public string title;
    public int status;
    public string detail;
}
