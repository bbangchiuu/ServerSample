#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    private async void Register()
    {
        await RunAsync("Register", async () =>
        {
            ApiResult<AccountResponse> result = await _api.RegisterAsync(_username, _password);
            if (Report(result))
                await OnLoggedIn(result.Data!);
        });
    }

    private async void Login()
    {
        await RunAsync("Login", async () =>
        {
            ApiResult<AccountResponse> result = await _api.LoginAsync(_username, _password);
            if (Report(result))
                await OnLoggedIn(result.Data!);
        });
    }

    private async Task OnLoggedIn(AccountResponse account)
    {
        _playerId = account.playerId;
        await RefreshAll();
    }

    private async void Refresh()
    {
        await RunAsync("Refresh", RefreshAll);
    }

    private async Task RefreshAll()
    {
        ApiResult<PlayerProfile> profile = await _api.GetProfileAsync(_playerId);
        if (Report(profile))
            _profile = profile.Data;

        ApiResult<InventoryResponse> inventory = await _api.GetInventoryAsync(_playerId);
        if (Report(inventory))
            _inventory = inventory.Data;
    }

    private async void ClaimDailyReward()
    {
        await RunAsync("Daily reward", async () =>
        {
            ApiResult<PlayerProfile> result = await _api.ClaimDailyRewardAsync(_playerId);
            if (Report(result))
            {
                _profile = result.Data;
                // Phần thưởng có thêm vật phẩm, nên tải lại túi đồ.
                ApiResult<InventoryResponse> inventory = await _api.GetInventoryAsync(_playerId);
                if (Report(inventory))
                    _inventory = inventory.Data;
            }
        });
    }

    private async void UseItem(string itemId)
    {
        await RunAsync($"Use {itemId}", async () =>
        {
            ApiResult<InventoryResponse> result = await _api.UseItemAsync(_playerId, itemId);
            if (Report(result))
                _inventory = result.Data;
        });
    }

    private async void ChangeDisplayName()
    {
        await RunAsync("Change name", async () =>
        {
            ApiResult<PlayerProfile> result = await _api.ChangeDisplayNameAsync(_playerId, _newDisplayName);
            if (Report(result))
                _profile = result.Data;
        });
    }

    /// <summary>
    /// Bọc mỗi hành động: khoá nút trong lúc chờ, và bắt mọi exception để lỗi không bị "nuốt mất"
    /// (exception trong async void không có ai bắt sẽ chỉ hiện trong Console).
    /// </summary>
    private async Task RunAsync(string actionName, Func<Task> action)
    {
        _busy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            AddLog($"{actionName}: unexpected error: {ex.Message}");
            Debug.LogException(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Ghi log kết quả một lần gọi API. Trả về true nếu thành công.</summary>
    private bool Report<T>(ApiResult<T> result)
    {
        if (result.IsSuccess)
        {
            AddLog($"OK {result.StatusCode}: {result.RawBody}");
            return true;
        }

        AddLog(result.StatusCode == 0
            ? $"Cannot reach server: {result.ErrorMessage}"
            : $"Error {result.StatusCode}: {result.ErrorMessage}");
        if (result.StatusCode == 400)
            AddLog($"   {result.RawBody}"); // lỗi validation: xem chi tiết từng trường
        return false;
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
}
