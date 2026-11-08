#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Màn hình thử HTTP API, vẽ bằng OnGUI nên không cần dựng UI trong scene.
/// Cách dùng: tạo một GameObject rỗng, Add Component → Api Test Controller, rồi bấm Play.
/// Nhớ chạy ServerSampleApi bằng profile "https" (https://localhost:5257).
/// Base Url được lưu trong scene: đổi server thì sửa trong Inspector, sửa giá trị mặc định ở đây không có tác dụng
/// với component đã gắn sẵn.
/// </summary>
public class ApiTestController : MonoBehaviour
{
    private const int MaxLogLines = 12;

    [SerializeField] private string baseUrl = "https://localhost:5257";

    private ApiClient _api = null!;

    // Ô nhập liệu
    private string _username = "bang";
    private string _password = "secret123";
    private string _newDisplayName = "";

    // Dữ liệu nhận về từ server
    private string _playerId = "";
    private PlayerProfile? _profile;
    private InventoryResponse? _inventory;

    // Đang chờ server trả lời: khoá các nút để người chơi không bấm liên tục.
    private bool _busy;
    private readonly List<string> _log = new();

    // Chat realtime qua hub SignalR
    private const int MaxChatLines = 10;
    private HubClient _hub = null!;
    private readonly List<string> _chat = new();
    private string _chatText = "";

    // Người chơi đang online: playerId → tên. Tải một lần sau khi kết nối, rồi cập nhật bằng PlayerOnline/PlayerOffline.
    private readonly Dictionary<string, string> _onlinePlayers = new();

    private void Awake()
    {
        _api = new ApiClient(baseUrl);

        // Mỗi lần kết nối (kể cả tự kết nối lại sau 15 phút), hub xin ApiClient một token CÒN HẠN:
        // token sắp hết hạn thì ApiClient làm mới bằng refresh token trước.
        _hub = new HubClient(baseUrl, () => _api.GetValidAccessTokenAsync());

        // Hàm SERVER gọi xuống (tên trùng IGameClient.ReceiveChatMessage ở server). Chạy trên main thread.
        _hub.On<ChatMessage>("ReceiveChatMessage", message =>
        {
            DateTime sentAt = DateTime.Parse(message.sentAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
            AddChatLine($"[{sentAt.ToLocalTime():HH:mm:ss}] {message.displayName}: {message.text}");
        });
        // Profile của CHÍNH mình thay đổi ở nơi khác (thiết bị khác nhận thưởng, đổi tên...): cập nhật luôn,
        // không cần bấm Refresh. Server chỉ gửi cái này cho các kết nối của đúng người chơi này.
        _hub.On<PlayerProfile>("ProfileChanged", profile =>
        {
            _profile = profile;
            AddLog($"(Pushed) Profile changed: {profile.displayName}, {profile.gold} gold");
        });

        // Có người (kể cả mình) vừa đổi số vàng trên bảng xếp hạng. Server gửi cho tất cả người đang online.
        _hub.On<LeaderboardEntry>("LeaderboardChanged", entry =>
            AddChatLine($"(Leaderboard) {entry.displayName} now has {entry.gold} gold, rank #{entry.rank}"));

        _hub.On<OnlinePlayer>("PlayerOnline", player =>
        {
            _onlinePlayers[player.playerId] = player.displayName;
            AddChatLine($"({player.displayName} is online)");
        });
        _hub.On<OnlinePlayer>("PlayerOffline", player =>
        {
            _onlinePlayers.Remove(player.playerId);
            AddChatLine($"({player.displayName} went offline)");
        });

        _hub.Reconnecting += error => AddChatLine($"(Chat connection lost: {error} Reconnecting...)");
        _hub.Reconnected += () =>
        {
            AddChatLine("(Chat reconnected)");
            // Trong lúc mất kết nối có thể đã lỡ các sự kiện PlayerOnline/PlayerOffline: tải lại cả danh sách.
            RefreshOnlinePlayers();
        };
        _hub.Closed += error => AddChatLine(error == null ? "(Chat disconnected)" : $"(Chat disconnected: {error})");
    }

    private void OnDestroy()
    {
        // Thoát Play Mode trong Editor KHÔNG tự đóng WebSocket: vòng lặp nhận dữ liệu và ping vẫn chạy ngầm.
        // Phải tự đóng ở đây.
        _ = _hub.CloseAsync();
    }

    // ===== Các hành động =====
    // Mỗi hành động: bật _busy (khoá nút) → gọi API → trong callback cập nhật dữ liệu, rồi tắt _busy.
    // Mọi nhánh kết thúc (thành công hay lỗi) đều phải tắt _busy, nếu không các nút bị khoá mãi.

    private void Register()
    {
        _busy = true;
        _api.Register(_username, _password,
            account =>
            {
                LogOk("Register", account);
                OnLoggedIn(account);
            },
            Fail("Register"));
    }

    private void Login()
    {
        _busy = true;
        _api.Login(_username, _password,
            account =>
            {
                LogOk("Login", account);
                OnLoggedIn(account);
            },
            Fail("Login"));
    }

    private void OnLoggedIn(AccountResponse account)
    {
        // Token đã được ApiClient giữ lại. _playerId chỉ để hiển thị, không gửi lên server nữa.
        // Access token hết hạn thì ApiClient tự làm mới bằng refresh token, màn hình này không cần làm gì.
        _playerId = account.playerId;
        AddLog($"Access token expires at {account.accessTokenExpiresAt}, refresh token at {account.refreshTokenExpiresAt} (UTC)");
        RefreshAll();
        ConnectWebSocket();
    }

    /// <summary>
    /// async void: được phép dùng cho hàm "bấm nút / sự kiện" như thế này, nhưng PHẢI tự bắt lỗi bên trong.
    /// Lỗi lọt ra khỏi async void không có ai bắt, chỉ hiện ra Console.
    /// </summary>
    private async void ConnectWebSocket()
    {
        try
        {
            await _hub.CloseAsync(); // đăng nhập lại tài khoản khác thì đóng kết nối cũ trước
            await _hub.ConnectAsync();
            AddChatLine("(Chat connected)");
            RefreshOnlinePlayers();
        }
        catch (Exception ex)
        {
            AddChatLine($"(Chat connect failed: {ex.Message})");
        }
    }

    /// <summary>
    /// Gọi hàm hub GetOnlinePlayers (hàm CÓ giá trị trả về) và thay toàn bộ danh sách online bằng kết quả.
    /// Gọi SAU khi đã kết nối và đăng ký PlayerOnline/PlayerOffline, để không lỡ ai vào/ra trong lúc chờ kết quả.
    /// </summary>
    private async void RefreshOnlinePlayers()
    {
        try
        {
            OnlinePlayersResponse response = await _hub.InvokeAsync<OnlinePlayersResponse>("GetOnlinePlayers");
            _onlinePlayers.Clear();
            foreach (OnlinePlayer player in response.players)
                _onlinePlayers[player.playerId] = player.displayName;
        }
        catch (Exception ex)
        {
            AddChatLine($"(Get online players failed: {ex.Message})");
        }
    }

    private async void SendChat()
    {
        string text = _chatText;
        _chatText = "";
        try
        {
            // Gọi GameHub.SendChatMessage trên server. Tin nhắn hiện ra khi server gửi lại qua ReceiveChatMessage,
            // giống mọi người khác, nên ở đây không tự thêm vào khung chat.
            await _hub.InvokeAsync("SendChatMessage", text);
        }
        catch (Exception ex)
        {
            AddChatLine($"(Send failed: {ex.Message})");
        }
    }

    private void AddChatLine(string line)
    {
        _chat.Add(line);
        if (_chat.Count > MaxChatLines)
            _chat.RemoveAt(0);
    }

    private void Logout()
    {
        _ = _hub.CloseAsync();
        _onlinePlayers.Clear();
        // ApiClient quên token và báo server thu hồi refresh token.
        _api.Logout();
        _playerId = "";
        _profile = null;
        _inventory = null;
        AddLog("Logged out");
    }

    private void Refresh()
    {
        RefreshAll();
    }

    /// <summary>
    /// Tải profile, XONG rồi mới tải túi đồ: muốn làm việc B sau việc A thì phải gọi B
    /// bên trong callback của A. Càng nhiều bước nối nhau thì callback càng lồng sâu.
    /// </summary>
    private void RefreshAll()
    {
        _busy = true;
        _api.GetProfile(
            profile =>
            {
                _profile = profile;
                _api.GetInventory(
                    inventory =>
                    {
                        _inventory = inventory;
                        LogOk("Refresh", profile);
                        _busy = false;
                    },
                    Fail("Get inventory"));
            },
            Fail("Get profile"));
    }

    private void ClaimDailyReward()
    {
        _busy = true;
        _api.ClaimDailyReward(
            profile =>
            {
                LogOk("Daily reward", profile);
                _profile = profile;
                // Phần thưởng có thêm vật phẩm, nên tải lại túi đồ.
                _api.GetInventory(
                    inventory =>
                    {
                        _inventory = inventory;
                        _busy = false;
                    },
                    Fail("Get inventory"));
            },
            Fail("Daily reward"));
    }

    private void UseItem(string itemId)
    {
        _busy = true;
        _api.UseItem(itemId,
            inventory =>
            {
                LogOk($"Use {itemId}", inventory);
                _inventory = inventory;
                _busy = false;
            },
            Fail($"Use {itemId}"));
    }

    private void ChangeDisplayName()
    {
        _busy = true;
        _api.ChangeDisplayName(_newDisplayName,
            profile =>
            {
                LogOk("Change name", profile);
                _profile = profile;
                _busy = false;
            },
            Fail("Change name"));
    }

    /// <summary>Ghi log thành công, kèm dữ liệu nhận được dưới dạng JSON.</summary>
    private void LogOk(string actionName, object data)
    {
        AddLog($"{actionName} OK: {JsonUtility.ToJson(data)}");
    }

    /// <summary>
    /// Tạo sẵn callback xử lý lỗi dùng chung cho mọi hành động: ghi log, rồi mở khoá các nút.
    /// Dùng: _api.Login(user, pass, onSuccess, Fail("Login"));
    /// </summary>
    private Action<ApiError> Fail(string actionName)
    {
        return error =>
        {
            AddLog(error.StatusCode == 0
                ? $"{actionName}: cannot reach server: {error.Message}"
                : $"{actionName}: error {error.StatusCode}: {error.Message}");
            if (error.StatusCode == 400)
                AddLog($"   {error.RawBody}"); // lỗi validation: xem chi tiết từng trường
            if (error.StatusCode == 401 && !_api.IsLoggedIn && _playerId != "")
            {
                // ApiClient đã thử làm mới token mà không được (refresh token hết hạn hoặc bị thu hồi)
                // và đã bỏ token: quay về màn hình đăng nhập.
                AddLog($"   {error.RawBody}"); // lý do server trả về
                Logout();
            }
            _busy = false;
        };
    }

    private void AddLog(string line)
    {
        Debug.Log($"[ApiTest] {line}");
        _log.Add(line);
        if (_log.Count > MaxLogLines)
            _log.RemoveAt(0);
    }

    // ===== Giao diện =====

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 640, Screen.height - 20), GUI.skin.box);
        GUILayout.Label($"Server: {baseUrl}");

        GUI.enabled = !_busy;

        GUILayout.BeginHorizontal();
        GUILayout.Label("Username", GUILayout.Width(70));
        _username = GUILayout.TextField(_username, GUILayout.Width(150));
        GUILayout.Label("Password", GUILayout.Width(70));
        _password = GUILayout.PasswordField(_password, '*', GUILayout.Width(150));
        if (GUILayout.Button("Register")) Register();
        if (GUILayout.Button("Login")) Login();
        GUILayout.EndHorizontal();

        if (_playerId != "")
        {
            GUILayout.Space(8);
            if (_profile != null)
            {
                string reward = string.IsNullOrEmpty(_profile.lastDailyRewardDate) ? "never" : _profile.lastDailyRewardDate;
                GUILayout.Label($"{_profile.displayName}  |  Level {_profile.level}  |  Gold {_profile.gold}  |  Last reward: {reward}");
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh")) Refresh();
            if (GUILayout.Button("Claim daily reward")) ClaimDailyReward();
            if (GUILayout.Button("Logout")) Logout();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("New name", GUILayout.Width(70));
            _newDisplayName = GUILayout.TextField(_newDisplayName, GUILayout.Width(150));
            if (GUILayout.Button("Change name")) ChangeDisplayName();
            GUILayout.EndHorizontal();

            GUILayout.Label("Inventory:");
            if (_inventory == null || _inventory.items == null || _inventory.items.Count == 0)
            {
                GUILayout.Label("   (empty)");
            }
            else
            {
                foreach (InventoryItem item in _inventory.items)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"   {item.itemId} x{item.quantity}", GUILayout.Width(150));
                    if (GUILayout.Button("Use", GUILayout.Width(60))) UseItem(item.itemId);
                    GUILayout.EndHorizontal();
                }
            }
        }

        GUI.enabled = true;

        if (_playerId != "")
        {
            // Chat không phụ thuộc _busy: đang chờ API khác vẫn chat được.
            GUILayout.Space(8);
            GUILayout.Label($"Online ({_onlinePlayers.Count}): {string.Join(", ", _onlinePlayers.Values)}");
            GUILayout.Label($"Chat ({_hub.State}):");
            foreach (string line in _chat)
                GUILayout.Label("   " + line);

            GUILayout.BeginHorizontal();
            _chatText = GUILayout.TextField(_chatText, 200, GUILayout.Width(400));
            GUI.enabled = _hub.IsConnected;
            if (GUILayout.Button("Send")) SendChat();
            // Đang tự kết nối lại thì không cho bấm: chỉ bấm được khi đã đóng hẳn (tự kết nối lại cũng bỏ cuộc).
            GUI.enabled = _hub.State == HubConnectionState.Disconnected;
            if (GUILayout.Button("Reconnect")) ConnectWebSocket();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8);
        GUILayout.Label(_busy ? "Waiting for server..." : "Log:");
        foreach (string line in _log)
            GUILayout.Label(line);

        GUILayout.EndArea();
    }


    // Mỗi tài khoản thử một ApiClient riêng, vì mỗi ApiClient giữ token của đúng MỘT người chơi.
    public List<string> listUser = new();
    private readonly List<ApiClient> _testClients = new();

    private void Update()
    {
        /*if (Input.GetKeyDown(KeyCode.Space))
        {
            for(int i = 0; i < 20; i++)
            {
                var data = new AccountRequest { username = $"bang{i}", password = _password };
                var client = new ApiClient(baseUrl);
                client.Register(data.username, data.password,
                    account => OnTestUserReady(client, account, "Register"),
                    error =>
                    {
                        // 409: tài khoản đã có từ lần chạy trước, đăng nhập lại để lấy token mới.
                        if (error.StatusCode == 409)
                            client.Login(data.username, data.password,
                                account => OnTestUserReady(client, account, "Login"),
                                loginError => AddLog($"[Space] Login {data.username}: error {loginError.StatusCode}: {loginError.Message}"));
                        else
                            AddLog($"[Space] Register {data.username}: error {error.StatusCode}: {error.Message}");
                    });
            }
        }
        else if (Input.GetKeyDown(KeyCode.A))
        {
            foreach (ApiClient client in _testClients)
            {
                client.ClaimDailyReward(onSuccess =>
                {
                    AddLog($"[A] {onSuccess.displayName} Claim Daily Reward  OK");
                },
                error =>
                {
                    AddLog($"[A] error {error.StatusCode}: {error.Message}");
                });
            }
        }
        else if (Input.GetKeyDown(KeyCode.B))
        {
            for (int i = 0; i < 20; i++)
            {
                ClaimDailyReward();
            }
        }*/
    }

    private void OnTestUserReady(ApiClient client, AccountResponse account, string actionName)
    {
        _testClients.Add(client);
        listUser.Add(account.playerId);
        AddLog($"[Space] {actionName} {account.username} OK");
    }
}
