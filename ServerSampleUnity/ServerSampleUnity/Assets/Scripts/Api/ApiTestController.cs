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

    private void Awake()
    {
        _api = new ApiClient(baseUrl);
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
        _playerId = account.playerId;
        RefreshAll();
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
        _api.GetProfile(_playerId,
            profile =>
            {
                _profile = profile;
                _api.GetInventory(_playerId,
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
        _api.ClaimDailyReward(_playerId,
            profile =>
            {
                LogOk("Daily reward", profile);
                _profile = profile;
                // Phần thưởng có thêm vật phẩm, nên tải lại túi đồ.
                _api.GetInventory(_playerId,
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
        _api.UseItem(_playerId, itemId,
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
        _api.ChangeDisplayName(_playerId, _newDisplayName,
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

        GUILayout.Space(8);
        GUILayout.Label(_busy ? "Waiting for server..." : "Log:");
        foreach (string line in _log)
            GUILayout.Label(line);

        GUILayout.EndArea();
    }


    public List<string> listUser = new();
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            for(int i = 0; i < 20; i++)
            {
                var data = new AccountRequest { username = $"bang{i}", password = _password };
                _api.Register(data.username, data.password,
                    account =>
                    {
                        listUser.Add(account.playerId);
                        AddLog($"[Space] Register {account.username} OK");
                    },
                    error => AddLog($"[Space] Register {data.username}: error {error.StatusCode}: {error.Message}"));
            }
        }
        else if (Input.GetKeyDown(KeyCode.A))
        {
            for (int i = 0; i < listUser.Count; i++)
            {
                var data = listUser[i];
                _api.ClaimDailyReward(data, onSuccess =>
                {
                    AddLog($"[Space] {onSuccess.displayName} Claim Daily Reward  OK");
                },
                error =>
                {
                    AddLog($"[Space] {data}: error {error.StatusCode}: {error.Message}");
                });
            }
        }
        else if (Input.GetKeyDown(KeyCode.B))
        {
            for (int i = 0; i < 20; i++)
            {
                ClaimDailyReward();
            }
        }
    }
}
