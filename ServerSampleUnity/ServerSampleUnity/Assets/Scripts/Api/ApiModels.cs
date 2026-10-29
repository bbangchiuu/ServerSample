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

/// <summary>Định dạng lỗi chuẩn mà server trả về cho mọi lỗi 4xx/5xx.</summary>
[Serializable]
public class ProblemDetails
{
    public string title;
    public int status;
    public string detail;
}
