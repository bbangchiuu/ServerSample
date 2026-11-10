#nullable enable
using System;
using System.Collections.Generic;

// Các class dữ liệu đi qua HUB khi dùng THƯ VIỆN SignalR chính thức (SignalRLibraryTest),
// dùng được với CẢ HAI giao thức JSON và MessagePack.
//
// Vì sao không dùng lại ChatMessage, OnlinePlayer... trong ApiModels.cs?
// Các class đó viết cho JsonUtility (field camelCase, ngày giờ là string). MessagePack của server lại gửi:
//  - tên khoá đúng như tên property C# của server: "DisplayName" (PascalCase), không phải "displayName";
//  - DateTime dạng TIMESTAMP nhị phân của MessagePack, không phải chuỗi "2026-11-10T...", nên field string
//    không đọc được;
//  - DateOnly dạng một số nguyên (số ngày), không phải chuỗi "2026-11-10".
//
// Ở đây dùng property PascalCase, đúng kiểu dữ liệu của server:
//  - MessagePack: khoá "DisplayName" khớp đúng property DisplayName (phân biệt hoa thường).
//  - JSON: server gửi "displayName", thư viện đọc KHÔNG phân biệt hoa thường nên vẫn khớp DisplayName.
//  - Property { get; set; } nên System.Text.Json đọc được mà không cần IncludeFields.
// Id (Guid ở server) để kiểu string: MessagePack và JSON đều gửi Guid dưới dạng chuỗi, so sánh với
// playerId (string) của ApiClient cho tiện. Khoá nào server gửi mà class không có thì bị bỏ qua.

/// <summary>Tin nhắn chat (thế giới hoặc clan). Giống Hubs/ChatMessage.cs ở server.</summary>
public class HubChatMessage
{
    public string PlayerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Giờ UTC server nhận tin. Kiểu DateTime thật: MessagePack gửi timestamp, JSON gửi chuỗi ISO, đều đọc được.</summary>
    public DateTime SentAt { get; set; }
}

public class HubOnlinePlayer
{
    public string PlayerId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>Kết quả hàm hub "GetOnlinePlayers".</summary>
public class HubOnlinePlayersResponse
{
    public List<HubOnlinePlayer> Players { get; set; } = new();
}

/// <summary>
/// Profile đẩy xuống qua "ProfileChanged". Cố ý KHÔNG có LastDailyRewardDate: kiểu DateOnly của server
/// đi qua JSON là chuỗi "2026-11-10" nhưng qua MessagePack là một số, không có một kiểu C# nào đọc được cả hai.
/// </summary>
public class HubPlayerProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Level { get; set; }
    public long Gold { get; set; }
}

/// <summary>Có người vào / rời clan ("ClanMemberJoined", "ClanMemberLeft"). Giống Hubs/ClanMemberEvent.cs.</summary>
public class HubClanMemberEvent
{
    public string ClanId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Chủ clan SAU thay đổi. null = clan đã bị xoá (người cuối cùng vừa rời).</summary>
    public string? LeaderId { get; set; }

    public int MemberCount { get; set; }
}
