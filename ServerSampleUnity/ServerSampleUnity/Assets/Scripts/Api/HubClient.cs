#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>Lỗi do hàm trên server ném ra (HubException), ví dụ tin nhắn rỗng hoặc quá dài.</summary>
public class HubInvocationException : Exception
{
    public HubInvocationException(string message) : base(message)
    {
    }
}

/// <summary>Trạng thái kết nối của HubClient.</summary>
public enum HubConnectionState
{
    /// <summary>Chưa kết nối, hoặc đã đóng hẳn (gọi CloseAsync, hoặc tự kết nối lại mãi không được).</summary>
    Disconnected,

    /// <summary>Đang kết nối lần đầu (ConnectAsync).</summary>
    Connecting,

    Connected,

    /// <summary>Vừa mất kết nối ngoài ý muốn, đang tự thử kết nối lại.</summary>
    Reconnecting,
}

/// <summary>
/// Client SignalR tự viết, tối giản: kết nối WebSocket tới hub (/hubs/game), gọi hàm của server,
/// và nhận các hàm server gọi xuống. Không cần thư viện SignalR nào, chỉ dùng ClientWebSocket có sẵn.
/// (Không chạy được trên WebGL: trình duyệt không cho dùng ClientWebSocket.)
///
/// Giao thức JSON của SignalR, mỗi gói tin là một chuỗi JSON kết thúc bằng byte 0x1E (Record Separator):
///   bắt tay:  client gửi {"protocol":"json","version":1}␞ ; server trả {}␞ (hoặc {"error":"..."}␞)
///   type 1  Invocation: gọi một hàm  {"type":1,"invocationId":"1","target":"SendChatMessage","arguments":["hi"]}
///   type 3  Completion: hàm đã chạy xong {"type":3,"invocationId":"1"} hoặc kèm "error"
///   type 6  Ping: "tôi vẫn còn sống". Server ngắt kết nối nếu 30 giây không nhận được gì từ client.
///   type 7  Close: server đóng kết nối, có thể kèm "error"
///
///     var hub = new HubClient("https://localhost:5257", () => api.GetValidAccessTokenAsync());
///     hub.On&lt;ChatMessage&gt;("ReceiveChatMessage", message => Debug.Log(message.text));
///     await hub.ConnectAsync();
///     await hub.InvokeAsync("SendChatMessage", "hello");
///
/// Giữ kết nối:
///  - Gửi ping nếu 15 giây chưa gửi gì, để server biết client còn sống.
///  - Server timeout: 30 giây không nhận được gì từ server (kể cả ping) thì coi như kết nối đã chết.
///    Mạng chết "im lặng" (rút dây, mất sóng) thì WebSocket không báo gì, chỉ có cách đếm thời gian này mới phát hiện được.
///  - Mất kết nối ngoài ý muốn thì tự kết nối lại sau 0, 2, 10, 30 giây (giống thư viện chính thức), rồi bỏ cuộc.
///
/// Gọi từ MAIN THREAD. Sau mỗi await, Unity đưa code quay về main thread, nên các handler đăng ký bằng On
/// và các sự kiện (Reconnecting, Reconnected, Closed) cũng chạy trên main thread: cập nhật giao diện trong đó là an toàn.
/// </summary>
public class HubClient
{
    private const byte RecordSeparator = 0x1E;

    // Loại gói tin (trường "type"), theo đặc tả giao thức SignalR (HubProtocol.md trong repo dotnet/aspnetcore).
    // Thư viện server có đúng các số này trong class HubProtocolConstants.
    // Các loại 2, 4, 5 (stream) và 8, 9 (ack/sequence khi kết nối lại) game chưa dùng nên không khai báo.
    private const int InvocationMessageType = 1; // gọi một hàm: "target" là tên hàm, "arguments" là tham số
    private const int CompletionMessageType = 3; // hàm đã chạy xong: kèm "result" hoặc "error", cùng invocationId
    private const int PingMessageType = 6;       // "tôi vẫn còn sống", không có dữ liệu
    private const int CloseMessageType = 7;      // server đóng kết nối, có thể kèm "error"

    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Chờ bao lâu trước MỖI lần thử kết nối lại. Hết danh sách mà vẫn không được thì bỏ cuộc.</summary>
    private static readonly TimeSpan[] ReconnectDelays =
    {
        TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    };

    private readonly Uri _url;
    private readonly Func<Task<string?>> _accessTokenProvider;

    /// <summary>Hàm nào server gọi xuống thì chạy handler nào. Key là tên hàm, ví dụ "ReceiveChatMessage".</summary>
    private readonly Dictionary<string, Action<string>> _handlers = new();

    /// <summary>Các lần gọi hàm server đang chờ kết quả (gói type 3). Key là invocationId.</summary>
    private readonly Dictionary<string, TaskCompletionSource<bool>> _pendingInvocations = new();

    /// <summary>
    /// ClientWebSocket chỉ cho MỘT lệnh SendAsync chạy tại một thời điểm. Vòng lặp ping và InvokeAsync có thể
    /// cùng gửi (cái này đang await thì cái kia tới lượt), nên phải xếp hàng: ai giữ "chìa khoá" thì mới được gửi.
    /// </summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<string?>? _handshake;
    private int _nextInvocationId;
    private DateTime _lastReceivedAt;
    private DateTime _lastSentAt;

    /// <param name="baseUrl">Địa chỉ server, ví dụ "https://localhost:5257". https tự đổi thành wss (WebSocket có mã hoá).</param>
    /// <param name="accessTokenProvider">
    /// Lấy access token CÒN HẠN mỗi lần kết nối (kể cả khi tự kết nối lại), ví dụ () => api.GetValidAccessTokenAsync().
    /// Trả null nghĩa là không còn đăng nhập: HubClient không thử kết nối lại nữa.
    /// </param>
    public HubClient(string baseUrl, Func<Task<string?>> accessTokenProvider)
    {
        string wsBaseUrl = baseUrl.TrimEnd('/')
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        _url = new Uri(wsBaseUrl + "/hubs/game");
        _accessTokenProvider = accessTokenProvider;
    }

    public HubConnectionState State { get; private set; } = HubConnectionState.Disconnected;

    public bool IsConnected => State == HubConnectionState.Connected;

    /// <summary>Mất kết nối ngoài ý muốn, bắt đầu tự kết nối lại. Tham số là lý do mất kết nối.</summary>
    public event Action<string>? Reconnecting;

    /// <summary>Tự kết nối lại thành công.</summary>
    public event Action? Reconnected;

    /// <summary>
    /// Kết nối đã đóng HẲN: gọi CloseAsync (tham số null), hoặc tự kết nối lại mãi không được (tham số là lỗi cuối cùng).
    /// Sau sự kiện này muốn kết nối lại thì phải gọi ConnectAsync.
    /// </summary>
    public event Action<string?>? Closed;

    /// <summary>
    /// Đăng ký hàm phía client cho server gọi. T là kiểu của tham số ĐẦU TIÊN (các hàm của game chỉ có 1 tham số).
    /// Tên phải trùng tên hàm trong IGameClient ở server.
    /// </summary>
    public void On<T>(string target, Action<T> handler)
    {
        _handlers[target] = json =>
        {
            // {"type":1,"target":"ReceiveChatMessage","arguments":[{...}]}: chỉ lấy phần tử đầu của "arguments".
            ServerInvocation<T> invocation = JsonUtility.FromJson<ServerInvocation<T>>(json);
            handler(invocation.arguments[0]);
        };
    }

    public async Task ConnectAsync()
    {
        if (State != HubConnectionState.Disconnected)
        {
            throw new InvalidOperationException($"Cannot connect while {State}. Call CloseAsync first.");
        }

        State = HubConnectionState.Connecting;
        try
        {
            await StartAsync();
        }
        catch
        {
            if (State == HubConnectionState.Connecting)
            {
                State = HubConnectionState.Disconnected;
            }
            throw;
        }

        if (State != HubConnectionState.Connecting)
        {
            // CloseAsync được gọi trong lúc đang kết nối: đóng luôn kết nối vừa mở.
            Cleanup();
            return;
        }
        State = HubConnectionState.Connected;
    }

    /// <summary>
    /// Gọi một hàm trên server với một tham số, chờ tới khi server chạy xong hàm đó.
    /// Server ném HubException thì ở đây ném HubInvocationException với đúng thông báo đó.
    /// </summary>
    public async Task InvokeAsync<TArg>(string target, TArg argument)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"Not connected ({State}).");
        }

        string invocationId = (++_nextInvocationId).ToString();
        var completion = new TaskCompletionSource<bool>();
        _pendingInvocations[invocationId] = completion;

        var invocation = new ClientInvocation<TArg>
        {
            type = InvocationMessageType,
            invocationId = invocationId,
            target = target,
            arguments = new[] { argument },
        };
        await SendRecordAsync(JsonUtility.ToJson(invocation));

        // Chờ gói type 3 có cùng invocationId (xử lý trong HandleRecord). Lỗi thì completion ném exception.
        await completion.Task;
    }

    /// <summary>Đóng kết nối và DỪNG tự kết nối lại (đăng xuất, tắt game...).</summary>
    public async Task CloseAsync()
    {
        HubConnectionState previousState = State;
        if (previousState == HubConnectionState.Disconnected)
        {
            return;
        }

        // Đổi trạng thái TRƯỚC tiên: vòng tự kết nối lại (nếu đang chạy) thấy trạng thái đổi thì tự dừng,
        // và vòng nhận dữ liệu thấy socket đóng cũng không coi đó là "mất kết nối ngoài ý muốn".
        State = HubConnectionState.Disconnected;

        ClientWebSocket? socket = _socket;
        if (socket != null && socket.State == WebSocketState.Open)
        {
            try
            {
                // Đóng "lịch sự": gửi yêu cầu đóng và CHỜ server xác nhận. Server treo hoặc mạng chết thì không bao giờ
                // có xác nhận, nên chỉ chờ tối đa 2 giây; quá hạn thì thư viện tự cắt ngang kết nối (abort).
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", timeout.Token);
            }
            catch (Exception)
            {
                // Đóng không được (quá hạn, mạng đã mất...) cũng không sao: bên dưới vẫn dọn dẹp.
            }
        }
        Cleanup();
        Closed?.Invoke(null);
    }

    // ===== Mở kết nối =====

    /// <summary>Mở WebSocket, bắt tay, rồi chạy vòng nhận dữ liệu và vòng giữ kết nối. Dùng cho cả kết nối lần đầu lẫn kết nối lại.</summary>
    private async Task StartAsync()
    {
        // Lấy token NGAY lúc kết nối: tự kết nối lại sau 15 phút thì token cũ đã hết hạn, provider sẽ làm mới trước.
        string? accessToken = await _accessTokenProvider();
        if (accessToken == null)
        {
            throw new NotLoggedInException();
        }

        var socket = new ClientWebSocket();
        // Ngoài trình duyệt thì đặt được header, nên gửi token trong header như các API (không để token nằm trên URL).
        socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
        _socket = socket;
        _cancellation = new CancellationTokenSource();
        _handshake = new TaskCompletionSource<string?>();

        try
        {
            // Mở WebSocket thẳng tới /hubs/game, bỏ qua bước "negotiate" mà thư viện chính thức hay làm trước
            // (hỏi server hỗ trợ những cách kết nối nào). Mình chỉ dùng WebSocket nên không cần hỏi.
            await socket.ConnectAsync(_url, _cancellation.Token);

            _lastReceivedAt = DateTime.UtcNow;
            _ = ReceiveLoopAsync(socket, _cancellation.Token);
            await SendRecordAsync("{\"protocol\":\"json\",\"version\":1}");

            Task finished = await Task.WhenAny(_handshake.Task, Task.Delay(HandshakeTimeout));
            if (finished != _handshake.Task)
            {
                throw new TimeoutException("SignalR handshake timed out.");
            }
            string? handshakeError = await _handshake.Task;
            if (handshakeError != null)
            {
                throw new InvalidOperationException("SignalR handshake failed: " + handshakeError);
            }
        }
        catch
        {
            // Lỗi giữa chừng (401 vì token sai, server tắt...): dọn dẹp để lần sau kết nối lại được.
            Cleanup();
            throw;
        }

        _ = KeepAliveLoopAsync(_cancellation.Token);
    }

    // ===== Mất kết nối và tự kết nối lại =====

    /// <summary>
    /// Kết nối đang dùng bị mất NGOÀI Ý MUỐN (server tắt, mất mạng, server timeout...).
    /// allowReconnect = false khi server nói rõ "đừng kết nối lại" (gói Close không có allowReconnect).
    /// </summary>
    private void OnConnectionLost(string error, bool allowReconnect)
    {
        if (State != HubConnectionState.Connected)
        {
            return; // đã xử lý rồi (vòng nhận dữ liệu và vòng giữ kết nối có thể cùng phát hiện), hoặc đang CloseAsync
        }

        Cleanup();
        if (allowReconnect)
        {
            ReconnectLoop(error);
        }
        else
        {
            State = HubConnectionState.Disconnected;
            Closed?.Invoke(error);
        }
    }

    /// <summary>
    /// Thử kết nối lại theo ReconnectDelays. async void vì không ai chờ nó: kết quả báo qua sự kiện
    /// Reconnected hoặc Closed. Vì là async void nên phải tự bắt mọi lỗi bên trong.
    /// </summary>
    private async void ReconnectLoop(string error)
    {
        State = HubConnectionState.Reconnecting;
        Reconnecting?.Invoke(error);

        foreach (TimeSpan delay in ReconnectDelays)
        {
            await Task.Delay(delay);
            if (State != HubConnectionState.Reconnecting)
            {
                return; // CloseAsync được gọi trong lúc chờ: dừng, CloseAsync đã báo Closed rồi
            }

            try
            {
                await StartAsync();
            }
            catch (NotLoggedInException)
            {
                error = "Not logged in.";
                break; // không còn token: thử thêm cũng vô ích
            }
            catch (Exception ex)
            {
                error = ex.Message; // server chưa bật lại, vẫn mất mạng...: chờ rồi thử lần sau
                continue;
            }

            if (State != HubConnectionState.Reconnecting)
            {
                Cleanup(); // CloseAsync được gọi trong lúc đang kết nối lại
                return;
            }
            State = HubConnectionState.Connected;
            Reconnected?.Invoke();
            return;
        }

        if (State == HubConnectionState.Reconnecting)
        {
            State = HubConnectionState.Disconnected;
            Closed?.Invoke(error);
        }
    }

    // ===== Nhận dữ liệu =====

    /// <summary>
    /// Đọc liên tục từ WebSocket cho tới khi đóng. Mỗi lần đọc nhận được một mẩu dữ liệu; mẩu đó có thể chứa
    /// nhiều gói tin, hoặc chỉ một phần của gói tin. Nên gom tất cả vào bộ đệm, rồi cắt ra ở từng byte 0x1E
    /// (giống tách gói tin TCP ở giai đoạn 1). Byte 0x1E không bao giờ xuất hiện bên trong một ký tự UTF-8 nhiều byte,
    /// nên cắt ở mức byte rồi mới đổi sang chữ là an toàn, kể cả với tiếng Việt có dấu.
    /// </summary>
    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var pending = new MemoryStream(); // phần đã nhận nhưng chưa gặp 0x1E
        string closeError = "Server closed the connection.";

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                _lastReceivedAt = DateTime.UtcNow; // nhận được BẤT CỨ thứ gì cũng chứng tỏ server còn sống
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                for (int i = 0; i < result.Count; i++)
                {
                    if (buffer[i] == RecordSeparator)
                    {
                        string json = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                        pending.SetLength(0);
                        HandleRecord(json);
                    }
                    else
                    {
                        pending.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // Cleanup đã huỷ: kết nối này đã được xử lý xong ở nơi khác
        }
        catch (Exception ex)
        {
            closeError = ex.Message; // mất mạng, server tắt đột ngột...
        }

        if (_socket != socket)
        {
            return; // không còn là kết nối hiện tại (đã đóng / đã thay bằng kết nối mới)
        }

        if (State == HubConnectionState.Connected)
        {
            OnConnectionLost(closeError, allowReconnect: true);
        }
        else
        {
            _handshake?.TrySetResult(closeError); // đóng ngay lúc đang bắt tay: báo cho StartAsync
        }
    }

    private void HandleRecord(string json)
    {
        // Gói đầu tiên là trả lời bắt tay: {} hoặc {"error":"..."}
        if (_handshake != null && !_handshake.Task.IsCompleted)
        {
            HubMessage handshake = JsonUtility.FromJson<HubMessage>(json);
            _handshake.TrySetResult(string.IsNullOrEmpty(handshake.error) ? null : handshake.error);
            return;
        }

        HubMessage message = JsonUtility.FromJson<HubMessage>(json);
        switch (message.type)
        {
            case InvocationMessageType: // server gọi một hàm của client
                if (_handlers.TryGetValue(message.target, out Action<string> handler))
                {
                    try
                    {
                        handler(json);
                    }
                    catch (Exception ex)
                    {
                        // Lỗi trong handler của bạn không được làm chết vòng lặp nhận dữ liệu.
                        Debug.LogException(ex);
                    }
                }
                else
                {
                    Debug.LogWarning($"[HubClient] No handler for '{message.target}'. Did you call On(\"{message.target}\", ...)?");
                }
                break;

            case CompletionMessageType: // hàm client đã gọi trên server chạy xong
                if (_pendingInvocations.TryGetValue(message.invocationId, out TaskCompletionSource<bool> completion))
                {
                    _pendingInvocations.Remove(message.invocationId);
                    if (string.IsNullOrEmpty(message.error))
                    {
                        completion.TrySetResult(true);
                    }
                    else
                    {
                        completion.TrySetException(new HubInvocationException(ReadHubError(message.error)));
                    }
                }
                break;

            case PingMessageType: // ping từ server: chỉ để giữ kết nối (_lastReceivedAt đã được cập nhật)
                break;

            case CloseMessageType: // server chủ động đóng kết nối
                // allowReconnect = true: server chỉ tạm đóng (ví dụ đang khởi động lại), kết nối lại được.
                // Không có allowReconnect: server không muốn client quay lại, kết nối lại cũng vô ích.
                OnConnectionLost(string.IsNullOrEmpty(message.error) ? "Server closed the connection." : message.error,
                    message.allowReconnect);
                break;
        }
    }

    /// <summary>
    /// Server gửi lỗi dạng "An unexpected error occurred invoking 'SendChatMessage' on the server. HubException: Message must be 1-200 characters."
    /// Chỉ giữ phần sau "HubException: " (đúng thông báo GameHub ném ra).
    /// </summary>
    private static string ReadHubError(string error)
    {
        const string marker = "HubException: ";
        int index = error.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? error.Substring(index + marker.Length) : error;
    }

    // ===== Giữ kết nối =====

    /// <summary>
    /// Mỗi giây kiểm tra một lần:
    ///  - 15 giây chưa GỬI gì thì gửi ping: server cần nghe thấy client ít nhất 30 giây một lần, nếu không sẽ ngắt kết nối.
    ///  - 30 giây chưa NHẬN được gì thì coi như server đã chết (server timeout). Server luôn gửi ping mỗi 15 giây,
    ///    nên im lặng gấp đôi thời gian đó nghĩa là có chuyện: mạng chết "im lặng", server treo...
    /// </summary>
    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                DateTime now = DateTime.UtcNow;

                if (now - _lastReceivedAt > ServerTimeout)
                {
                    OnConnectionLost($"Server timeout: no data for {ServerTimeout.TotalSeconds} seconds.", allowReconnect: true);
                    return;
                }

                if (now - _lastSentAt >= PingInterval)
                {
                    await SendRecordAsync($"{{\"type\":{PingMessageType}}}");
                }
            }
        }
        catch (Exception)
        {
            // Bị huỷ (Cleanup) hoặc gửi lỗi (mất mạng): ReceiveLoopAsync hoặc lần kiểm tra timeout sẽ xử lý.
        }
    }

    // ===== Gửi dữ liệu =====

    private async Task SendRecordAsync(string json)
    {
        ClientWebSocket socket = _socket ?? throw new InvalidOperationException("Not connected.");
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[jsonBytes.Length + 1];
        Buffer.BlockCopy(jsonBytes, 0, bytes, 0, jsonBytes.Length);
        bytes[bytes.Length - 1] = RecordSeparator;

        await _sendLock.WaitAsync();
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            _lastSentAt = DateTime.UtcNow;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ===== Dọn dẹp =====

    /// <summary>Huỷ các vòng lặp, bỏ socket hiện tại. Không đổi State và không báo sự kiện: nơi gọi tự lo.</summary>
    private void Cleanup()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _socket?.Dispose();
        _socket = null;
        _handshake?.TrySetResult("Connection closed.");

        // Các lần gọi đang chờ kết quả sẽ không bao giờ có kết quả nữa: báo lỗi để chúng không treo mãi.
        foreach (TaskCompletionSource<bool> completion in _pendingInvocations.Values)
        {
            completion.TrySetException(new InvalidOperationException("Connection closed."));
        }
        _pendingInvocations.Clear();
    }

    /// <summary>accessTokenProvider trả null: không còn đăng nhập.</summary>
    private class NotLoggedInException : Exception
    {
        public NotLoggedInException() : base("Not logged in.")
        {
        }
    }

    // ===== Các class JSON (JsonUtility chỉ đọc/ghi FIELD public) =====

    /// <summary>Các trường chung của mọi gói tin, đủ để biết đây là gói gì.</summary>
    [Serializable]
    private class HubMessage
    {
        public int type = 0; // gán sẵn để trình biên dịch khỏi cảnh báo "không ai gán": JsonUtility mới là bên gán
        public string target = "";
        public string invocationId = "";
        public string error = "";
        public bool allowReconnect = false; // chỉ có trong gói Close (type 7)
    }

    [Serializable]
    private class ServerInvocation<T>
    {
        public T[] arguments = Array.Empty<T>();
    }

    [Serializable]
    private class ClientInvocation<T>
    {
        public int type;
        public string invocationId = "";
        public string target = "";
        public T[] arguments = Array.Empty<T>();
    }
}
