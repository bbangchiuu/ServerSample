#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR.Client;
using UnityEngine;

/// <summary>
/// Phần CLAN của SignalRLibraryTest (vẽ ở cột bên phải): tìm, tạo, vào, rời clan và chat clan.
///
/// "partial class": cùng MỘT class SignalRLibraryTest nhưng viết ở hai file. Khi compile hai file được gộp lại,
/// nên ở đây dùng thẳng _api, _connection, _playerId, RunOnMainThread... của file SignalRLibraryTest.cs.
/// </summary>
public partial class SignalRLibraryTest
{
    private const int MaxClanChatLines = 10;

    /// <summary>Clan của mình. null = chưa ở clan nào (hoặc chưa tải xong).</summary>
    private ClanResponse? _clan;

    /// <summary>Đang chờ server trả lời một API clan: khoá các nút clan để không bấm liên tục.</summary>
    private bool _clanBusy;

    private string _newClanName = "";
    private string _clanSearch = "";
    private List<ClanSummary> _clanSearchResults = new();
    private readonly List<string> _clanChat = new();
    private string _clanChatText = "";

    /// <summary>
    /// Đăng ký các hàm server gọi xuống cho clan. Gọi mỗi lần tạo HubConnection mới (trong Connect).
    /// Thư viện gọi các hàm này trên THREAD KHÁC: mọi việc đều bọc trong RunOnMainThread.
    /// </summary>
    private void RegisterClanHandlers(HubConnection connection)
    {
        connection.On<ChatMessage>("ReceiveClanChatMessage", message =>
            RunOnMainThread(() => AddClanChatLine($"{message.displayName}: {message.text}")));

        connection.On<ClanMemberEvent>("ClanMemberJoined", e => RunOnMainThread(() => OnClanMemberJoined(e)));
        connection.On<ClanMemberEvent>("ClanMemberLeft", e => RunOnMainThread(() => OnClanMemberLeft(e)));
    }

    private void OnClanMemberJoined(ClanMemberEvent e)
    {
        if (e.playerId == _playerId)
        {
            // Chính mình vừa vào/tạo clan, có thể là trên THIẾT BỊ KHÁC: tải cả clan
            // (sự kiện không kèm danh sách thành viên).
            RefreshMyClan();
            return;
        }

        if (_clan == null || e.clanId != _clan.id)
        {
            return;
        }

        // Người khác vào clan của mình: tự thêm vào danh sách, không cần gọi lại API.
        _clan.members.Add(new ClanMember
        {
            playerId = e.playerId,
            displayName = e.displayName,
            joinedAt = DateTime.UtcNow.ToString("o"),
        });
        ApplyClanChange(e);
        AddClanChatLine($"({e.displayName} joined the clan)");
    }

    private void OnClanMemberLeft(ClanMemberEvent e)
    {
        if (e.playerId == _playerId)
        {
            // Chính mình vừa rời clan (có thể trên thiết bị khác).
            if (_clan != null)
            {
                AddLine($"Left clan {_clan.name}");
            }
            _clan = null;
            _clanChat.Clear();
            return;
        }

        if (_clan == null || e.clanId != _clan.id)
        {
            return;
        }

        _clan.members.RemoveAll(m => m.playerId == e.playerId);
        string oldLeaderId = _clan.leaderId;
        ApplyClanChange(e);
        AddClanChatLine($"({e.displayName} left the clan)");
        if (_clan.leaderId != oldLeaderId)
        {
            ClanMember? leader = _clan.members.Find(m => m.playerId == _clan.leaderId);
            AddClanChatLine($"({leader?.displayName ?? "?"} is now the leader)");
        }
    }

    /// <summary>Cập nhật chủ clan và số thành viên theo sự kiện (server gửi kèm giá trị SAU thay đổi).</summary>
    private void ApplyClanChange(ClanMemberEvent e)
    {
        _clan!.leaderId = e.leaderId;
        _clan.memberCount = e.memberCount;
        foreach (ClanMember member in _clan.members)
        {
            member.isLeader = member.playerId == e.leaderId;
        }
    }

    /// <summary>
    /// Tải clan của mình: sau khi đăng nhập, và sau khi kết nối lại (lúc mất kết nối có thể đã lỡ sự kiện).
    /// 404 nghĩa là chưa ở clan nào, không phải lỗi. Phải gọi trên main thread (ApiClient dùng UnityWebRequest).
    /// </summary>
    private void RefreshMyClan()
    {
        _api.GetMyClan(
            clan => _clan = clan,
            error =>
            {
                if (error.StatusCode == 404)
                {
                    _clan = null;
                }
                else
                {
                    AddLine($"Get my clan failed ({error.StatusCode}): {error.Message}");
                }
            });
    }

    private void SearchClans()
    {
        _clanBusy = true;
        _api.SearchClans(_clanSearch.Trim(),
            result =>
            {
                _clanSearchResults = result.clans;
                _clanBusy = false;
            },
            ClanFail("Search clans"));
    }

    private void CreateClan()
    {
        _clanBusy = true;
        _api.CreateClan(_newClanName,
            clan =>
            {
                AddLine($"Created clan {clan.name}");
                OnEnteredClan(clan);
            },
            ClanFail("Create clan"));
    }

    private void JoinClan(string clanId)
    {
        _clanBusy = true;
        _api.JoinClan(clanId,
            clan =>
            {
                AddLine($"Joined clan {clan.name}");
                OnEnteredClan(clan);
            },
            ClanFail("Join clan"));
    }

    private void OnEnteredClan(ClanResponse clan)
    {
        _clan = clan;
        _clanChat.Clear();
        _clanSearchResults.Clear();
        _newClanName = "";
        _clanBusy = false;
    }

    private void LeaveClan()
    {
        _clanBusy = true;
        _api.LeaveClan(
            _ =>
            {
                // Server cũng gửi ClanMemberLeft cho chính mình, nhưng xoá luôn ở đây để giao diện đổi ngay.
                AddLine($"Left clan {_clan?.name}");
                _clan = null;
                _clanChat.Clear();
                _clanBusy = false;
            },
            ClanFail("Leave clan"));
    }

    private async void SendClanChat()
    {
        if (!IsHubConnected)
        {
            return;
        }

        string text = _clanChatText;
        _clanChatText = "";
        try
        {
            // Gọi GameHub.SendClanChatMessage. Tin hiện ra khi server gửi lại qua ReceiveClanChatMessage.
            // Không ở clan nào thì server ném HubException("You are not in a clan.") và nhảy vào catch.
            await _connection!.InvokeAsync("SendClanChatMessage", text);
        }
        catch (Exception e)
        {
            AddClanChatLine($"(Send failed: {e.Message})");
        }
    }

    private void AddClanChatLine(string line)
    {
        _clanChat.Add(line);
        if (_clanChat.Count > MaxClanChatLines)
        {
            _clanChat.RemoveAt(0);
        }
    }

    /// <summary>Callback lỗi dùng chung cho các API clan: ghi lại lỗi rồi mở khoá các nút.</summary>
    private Action<ApiError> ClanFail(string actionName)
    {
        return error =>
        {
            AddLine($"{actionName} failed ({error.StatusCode}): {error.Message}");
            _clanBusy = false;
        };
    }

    // ===== Giao diện (cột bên phải) =====

    private void DrawClanPanel()
    {
        if (_playerId == "")
        {
            return;
        }

        GUILayout.BeginArea(new Rect(520, 10, 460, 600), GUI.skin.box);
        GUI.enabled = !_clanBusy;

        if (_clan == null)
        {
            DrawNoClan();
        }
        else
        {
            DrawMyClan(_clan);
        }

        GUI.enabled = true;
        GUILayout.EndArea();
    }

    private void DrawNoClan()
    {
        GUILayout.Label("Clan: (none)");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(50));
        _newClanName = GUILayout.TextField(_newClanName, 20, GUILayout.Width(200));
        if (GUILayout.Button("Create clan")) CreateClan();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        GUILayout.Label("Search", GUILayout.Width(50));
        _clanSearch = GUILayout.TextField(_clanSearch, 20, GUILayout.Width(200));
        if (GUILayout.Button("Search")) SearchClans();
        GUILayout.EndHorizontal();

        foreach (ClanSummary clan in _clanSearchResults)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"   {clan.name} ({clan.memberCount}/{clan.maxMembers})", GUILayout.Width(300));
            GUI.enabled = !_clanBusy && clan.memberCount < clan.maxMembers;
            if (GUILayout.Button("Join", GUILayout.Width(60))) JoinClan(clan.id);
            GUI.enabled = !_clanBusy;
            GUILayout.EndHorizontal();
        }
    }

    private void DrawMyClan(ClanResponse clan)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Clan: {clan.name} ({clan.memberCount}/{clan.maxMembers})");
        if (GUILayout.Button("Leave", GUILayout.Width(80))) LeaveClan();
        GUILayout.EndHorizontal();

        foreach (ClanMember member in clan.members)
        {
            string leader = member.isLeader ? "  [Leader]" : "";
            string me = member.playerId == _playerId ? "  (you)" : "";
            GUILayout.Label($"   {member.displayName}{leader}{me}");
        }

        // Chat clan không bị khoá khi đang chờ API clan.
        GUI.enabled = true;
        GUILayout.Space(8);
        GUILayout.Label("Clan chat:");
        foreach (string line in _clanChat)
        {
            GUILayout.Label("   " + line);
        }

        GUILayout.BeginHorizontal();
        _clanChatText = GUILayout.TextField(_clanChatText, 200, GUILayout.Width(330));
        GUI.enabled = IsHubConnected;
        if (GUILayout.Button("Send")) SendClanChat();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
    }
}
