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
///     var hub = new HubClient("https://localhost:5257", () => api.AccessToken);
///     hub.On&lt;ChatMessage&gt;("ReceiveChatMessage", message => Debug.Log(message.text));
///     await hub.ConnectAsync();
///     await hub.InvokeAsync("SendChatMessage", "hello");
///
/// Gọi từ MAIN THREAD. Sau mỗi await, Unity đưa code quay về main thread, nên các handler đăng ký bằng On
/// cũng chạy trên main thread: cập nhật giao diện trong đó là an toàn.
/// </summary>
public class HubClient
{
    private const byte RecordSeparator = 0x1E;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly Uri _url;
    private readonly Func<string?> _accessTokenProvider;

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

    /// <param name="baseUrl">Địa chỉ server, ví dụ "https://localhost:5257". https tự đổi thành wss (WebSocket có mã hoá).</param>
    /// <param name="accessTokenProvider">Lấy access token MỚI NHẤT lúc kết nối, ví dụ () => api.AccessToken.</param>
    public HubClient(string baseUrl, Func<string?> accessTokenProvider)
    {
        string wsBaseUrl = baseUrl.TrimEnd('/')
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        _url = new Uri(wsBaseUrl + "/hubs/game");
        _accessTokenProvider = accessTokenProvider;
    }

    public bool IsConnected { get; private set; }

    /// <summary>Kết nối bị đóng (server đóng, mất mạng, hoặc gọi CloseAsync). Tham số là lý do lỗi, null nếu đóng bình thường.</summary>
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
        if (_socket != null)
        {
            throw new InvalidOperationException("Already connected. Call CloseAsync first.");
        }

        string? accessToken = _accessTokenProvider();
        if (accessToken == null)
        {
            throw new InvalidOperationException("Not logged in.");
        }

        _socket = new ClientWebSocket();
        // Ngoài trình duyệt thì đặt được header, nên gửi token trong header như các API (không để token nằm trên URL).
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
        _cancellation = new CancellationTokenSource();
        _handshake = new TaskCompletionSource<string?>();

        try
        {
            // Mở WebSocket thẳng tới /hubs/game, bỏ qua bước "negotiate" mà thư viện chính thức hay làm trước
            // (hỏi server hỗ trợ những cách kết nối nào). Mình chỉ dùng WebSocket nên không cần hỏi.
            await _socket.ConnectAsync(_url, _cancellation.Token);

            _ = ReceiveLoopAsync(_socket, _cancellation.Token);
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
            // Lỗi giữa chừng (401 vì token sai/hết hạn, server tắt...): dọn dẹp để gọi ConnectAsync lại được.
            Cleanup();
            throw;
        }

        IsConnected = true;
        _ = PingLoopAsync(_cancellation.Token);
    }

    /// <summary>
    /// Gọi một hàm trên server với một tham số, chờ tới khi server chạy xong hàm đó.
    /// Server ném HubException thì ở đây ném HubInvocationException với đúng thông báo đó.
    /// </summary>
    public async Task InvokeAsync<TArg>(string target, TArg argument)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected.");
        }

        string invocationId = (++_nextInvocationId).ToString();
        var completion = new TaskCompletionSource<bool>();
        _pendingInvocations[invocationId] = completion;

        var invocation = new ClientInvocation<TArg>
        {
            type = 1,
            invocationId = invocationId,
            target = target,
            arguments = new[] { argument },
        };
        await SendRecordAsync(JsonUtility.ToJson(invocation));

        // Chờ gói type 3 có cùng invocationId (xử lý trong HandleRecord). Lỗi thì completion ném exception.
        await completion.Task;
    }

    public async Task CloseAsync()
    {
        ClientWebSocket? socket = _socket;
        if (socket == null)
        {
            return;
        }

        if (socket.State == WebSocketState.Open)
        {
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
            }
            catch (Exception)
            {
                // Đóng không được (mạng đã mất...) cũng không sao: bên dưới vẫn dọn dẹp.
            }
        }
        OnClosed(null);
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
        string? closeError = null;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
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
            // CloseAsync hoặc tắt game: dừng đọc.
        }
        catch (Exception ex)
        {
            closeError = ex.Message; // mất mạng, server tắt đột ngột...
        }

        if (_socket == socket)
        {
            OnClosed(closeError);
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
            case 1: // Invocation: server gọi một hàm của client
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

            case 3: // Completion: hàm client đã gọi trên server chạy xong
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

            case 6: // Ping từ server: chỉ để giữ kết nối, không cần làm gì
                break;

            case 7: // Close: server chủ động đóng kết nối
                OnClosed(string.IsNullOrEmpty(message.error) ? null : message.error);
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

    // ===== Gửi dữ liệu =====

    /// <summary>
    /// Server cần nghe thấy client ít nhất 30 giây một lần, nếu không sẽ coi như client đã chết và ngắt kết nối.
    /// Lúc người chơi không chat gì, gói ping mỗi 15 giây giữ cho kết nối còn sống.
    /// </summary>
    private async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, cancellationToken);
                await SendRecordAsync("{\"type\":6}");
            }
        }
        catch (Exception)
        {
            // Bị huỷ (đóng kết nối) hoặc gửi lỗi (mất mạng): ReceiveLoopAsync sẽ báo Closed.
        }
    }

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
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ===== Đóng =====

    /// <summary>Dọn dẹp và báo sự kiện Closed đúng một lần.</summary>
    private void OnClosed(string? error)
    {
        bool wasConnected = IsConnected;
        Cleanup();
        if (wasConnected)
        {
            Closed?.Invoke(error);
        }
    }

    private void Cleanup()
    {
        IsConnected = false;
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

    // ===== Các class JSON (JsonUtility chỉ đọc/ghi FIELD public) =====

    /// <summary>Các trường chung của mọi gói tin, đủ để biết đây là gói gì.</summary>
    [Serializable]
    private class HubMessage
    {
        public int type = 0; // gán sẵn để trình biên dịch khỏi cảnh báo "không ai gán": JsonUtility mới là bên gán
        public string target = "";
        public string invocationId = "";
        public string error = "";
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
