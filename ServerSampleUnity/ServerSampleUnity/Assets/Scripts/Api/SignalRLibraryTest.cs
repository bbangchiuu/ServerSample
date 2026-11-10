#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using UnityEngine;

/// <summary>
/// Thử hub /hubs/game bằng thư viện CHÍNH THỨC Microsoft.AspNetCore.SignalR.Client, thay cho HubClient tự viết.
/// Cách dùng: tạo một GameObject rỗng, Add Component → SignalR Library Test, bấm Play rồi bấm Login.
/// Đăng nhập vẫn dùng ApiClient (HTTP), chỉ phần realtime là dùng thư viện.
/// "partial": phần clan (cột bên phải) nằm ở file SignalRLibraryTest.Clan.cs, cùng một class.
/// </summary>
public partial class SignalRLibraryTest : MonoBehaviour
{
    [SerializeField] private string baseUrl = "https://localhost:5257";
    [SerializeField] private string username = "bang";
    [SerializeField] private string password = "secret123";

    private const int MaxLines = 15;

    private ApiClient _api = null!;
    private HubConnection? _connection;

    /// <summary>playerId của người đang đăng nhập. "" = chưa đăng nhập.</summary>
    private string _playerId = "";

    // Main thread của Unity. Thư viện gọi các hàm đăng ký bằng On(...) trên một THREAD KHÁC (thread pool),
    // mà phần lớn API của Unity chỉ được gọi trên main thread: phải chuyển việc về đây trước khi xử lý.
    private SynchronizationContext _mainThread = null!;

    private readonly List<string> _lines = new();
    private string _chatText = "";

    private void Awake()
    {
        // Awake chạy trên main thread: SynchronizationContext.Current lúc này là UnitySynchronizationContext.
        _mainThread = SynchronizationContext.Current!;
        _api = new ApiClient(baseUrl);
    }

    private void OnDestroy()
    {
        // Thoát Play Mode không tự đóng kết nối: phải tự đóng, giống HubClient.
        if (_connection != null)
        {
            _ = _connection.DisposeAsync();
        }
    }

    private void Login()
    {
        _api.Login(username, password,
            account =>
            {
                _playerId = account.playerId;
                RefreshMyClan();
                Connect();
            },
            error => AddLine($"Login failed: {error.Message}"));
    }

    private async void Connect()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(baseUrl.TrimEnd('/') + "/hubs/game", options =>
            {
                // Mỗi lần kết nối (kể cả tự kết nối lại), thư viện xin một token còn hạn, giống HubClient.
                options.AccessTokenProvider = () => _api.GetValidAccessTokenAsync();
                // Đi thẳng WebSocket, bỏ bước negotiate (POST /hubs/game/negotiate), giống HubClient tự viết.
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            // Model trong ApiModels.cs dùng FIELD public (vì JsonUtility), còn System.Text.Json mặc định
            // chỉ đọc/ghi PROPERTY. Không bật IncludeFields thì mọi field đều null/0.
            .AddJsonProtocol(options => options.PayloadSerializerOptions.IncludeFields = true)
            // Mất kết nối thì tự thử lại sau 0s, 2s, 10s, 30s rồi bỏ cuộc (đúng dãy HubClient tự viết đã học theo).
            .WithAutomaticReconnect()
            .Build();

        // Các hàm SERVER gọi xuống: tên trùng IGameClient ở server.
        _connection.On<ChatMessage>("ReceiveChatMessage", message =>
            RunOnMainThread(() => AddLine($"{message.displayName}: {message.text}")));
        _connection.On<OnlinePlayer>("PlayerOnline", player =>
            RunOnMainThread(() => AddLine($"({player.displayName} is online)")));
        _connection.On<OnlinePlayer>("PlayerOffline", player =>
            RunOnMainThread(() => AddLine($"({player.displayName} went offline)")));
        _connection.On<PlayerProfile>("ProfileChanged", profile =>
            RunOnMainThread(() => AddLine($"(Pushed) Profile: {profile.displayName}, {profile.gold} gold")));

        // Mỗi lần Connect tạo một HubConnection MỚI, nên phải đăng ký lại các hàm của clan cho kết nối mới này.
        RegisterClanHandlers(_connection);

        // Các sự kiện kết nối: thư viện cũng gọi chúng trên thread khác, và yêu cầu trả về Task.
        _connection.Reconnecting += error =>
        {
            RunOnMainThread(() => AddLine($"(Reconnecting: {error?.Message})"));
            return Task.CompletedTask;
        };
        _connection.Reconnected += connectionId =>
        {
            RunOnMainThread(() =>
            {
                AddLine("(Reconnected)");
                // Lúc mất kết nối có thể đã lỡ ClanMemberJoined/Left: tải lại clan.
                // Phải ở trong RunOnMainThread: ApiClient dùng UnityWebRequest, chỉ gọi được trên main thread.
                RefreshMyClan();
            });
            return Task.CompletedTask;
        };
        _connection.Closed += error =>
        {
            RunOnMainThread(() => AddLine(error == null ? "(Closed)" : $"(Closed: {error.Message})"));
            return Task.CompletedTask;
        };

        try
        {
            await _connection.StartAsync();
            // Sau await này đã quay về main thread: await trong MonoBehaviour tự quay về UnitySynchronizationContext.
            AddLine("Connected");

            // Hàm hub CÓ giá trị trả về: InvokeAsync<T> chờ Completion từ server.
            OnlinePlayersResponse online = await _connection.InvokeAsync<OnlinePlayersResponse>("GetOnlinePlayers");
            AddLine($"Online: {online.players.Count} player(s)");
        }
        catch (Exception e)
        {
            AddLine($"Connect failed: {e.Message}");
        }
    }

    private async void SendChat()
    {
        // Ghi tên đầy đủ: HubClient.cs tự viết cũng có một enum tên HubConnectionState (ở global namespace),
        // và C# tìm trong namespace hiện tại TRƯỚC các dòng using, nên viết tắt sẽ ra enum của HubClient.
        if (_connection == null || _connection.State != Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            return;
        }

        string text = _chatText;
        _chatText = "";
        try
        {
            // Hàm hub không trả về giá trị: InvokeAsync vẫn chờ server xác nhận, và ném HubException nếu server báo lỗi.
            await _connection.InvokeAsync("SendChatMessage", text);
        }
        catch (Exception e)
        {
            AddLine($"Send failed: {e.Message}");
        }
    }

    private void RunOnMainThread(Action action)
    {
        _mainThread.Post(_ => action(), null);
    }

    private void AddLine(string line)
    {
        _lines.Add(line);
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, 500, 600));
        GUILayout.Label($"Server: {baseUrl}   State: {_connection?.State.ToString() ?? "-"}");
        if (GUILayout.Button("Login + Connect"))
        {
            Login();
        }

        GUILayout.BeginHorizontal();
        _chatText = GUILayout.TextField(_chatText, GUILayout.Width(380));
        if (GUILayout.Button("Send"))
        {
            SendChat();
        }
        GUILayout.EndHorizontal();

        foreach (string line in _lines)
        {
            GUILayout.Label(line);
        }
        GUILayout.EndArea();

        DrawClanPanel(); // cột bên phải, xem SignalRLibraryTest.Clan.cs
    }

    /// <summary>Kết nối hub đang mở và dùng được (gửi tin, gọi hàm).</summary>
    private bool IsHubConnected =>
        _connection != null && _connection.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected;
}
