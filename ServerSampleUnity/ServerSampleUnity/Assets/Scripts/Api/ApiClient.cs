#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Thông tin lỗi khi gọi API thất bại.</summary>
public class ApiError
{
    public ApiError(long statusCode, string message, string rawBody)
    {
        StatusCode = statusCode;
        Message = message;
        RawBody = rawBody;
    }

    /// <summary>HTTP status code (400, 401, 404, 409...). 0 nếu không kết nối được tới server.</summary>
    public long StatusCode { get; }

    /// <summary>Thông báo lỗi dễ đọc, lấy từ "title"/"detail" của ProblemDetails.</summary>
    public string Message { get; }

    /// <summary>Nội dung gốc server trả về, ví dụ danh sách lỗi validation của từng trường.</summary>
    public string RawBody { get; }
}

/// <summary>
/// Gọi HTTP API của ServerSampleApi bằng UnityWebRequest, kiểu callback:
/// mỗi API nhận onSuccess (gọi khi thành công, kèm dữ liệu) và onError (gọi khi thất bại).
///
///     api.Register("bang", "secret123",
///         account => Debug.Log(account.playerId),
///         error => Debug.Log(error.Message));
///
/// Phải gọi từ MAIN THREAD (ví dụ từ MonoBehaviour): UnityWebRequest chỉ dùng được trên main thread.
/// Các callback cũng được gọi trên main thread, nên cập nhật giao diện trong callback là an toàn.
/// Bỏ trống onError thì lỗi được ghi ra Console bằng Debug.LogWarning, không bị im lặng.
///
/// Đăng nhập: Register/Login thành công thì ApiClient TỰ GIỮ token (JWT) và tự gửi kèm
/// "Authorization: Bearer &lt;token&gt;" cho các API của người chơi. Server lấy playerId từ token,
/// nên các hàm bên dưới không cần (và không nhận) playerId nữa.
/// Mỗi ApiClient giữ token của MỘT người chơi. Muốn thử nhiều người chơi cùng lúc thì tạo nhiều ApiClient.
/// </summary>
public class ApiClient
{
    private readonly string _baseUrl;

    public ApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// JWT của người chơi đang đăng nhập, null nếu chưa đăng nhập.
    /// Chỉ giữ trong RAM: tắt game là mất, mở lại phải đăng nhập lại.
    /// </summary>
    public string? AccessToken { get; private set; }

    public bool IsLoggedIn => AccessToken != null;

    /// <summary>Quên token. Server không lưu token nên không cần báo cho server (bước 5 sẽ làm đăng xuất thật).</summary>
    public void Logout()
    {
        AccessToken = null;
    }

    // ===== Tài khoản (không cần đăng nhập) =====

    public void Register(string username, string password, Action<AccountResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send<AccountResponse>("POST", "/api/accounts/register", new AccountRequest { username = username, password = password },
            requiresLogin: false, account => OnLoggedIn(account, onSuccess), onError);

    public void Login(string username, string password, Action<AccountResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send<AccountResponse>("POST", "/api/accounts/login", new AccountRequest { username = username, password = password },
            requiresLogin: false, account => OnLoggedIn(account, onSuccess), onError);

    /// <summary>Giữ token lại TRƯỚC khi báo cho người gọi, để trong onSuccess gọi ngay API cần đăng nhập là được.</summary>
    private void OnLoggedIn(AccountResponse account, Action<AccountResponse> onSuccess)
    {
        AccessToken = account.accessToken;
        onSuccess(account);
    }

    // ===== Người chơi đang đăng nhập (/api/players/me) =====

    public void GetProfile(Action<PlayerProfile> onSuccess, Action<ApiError>? onError = null) =>
        Send("GET", "/api/players/me", null, requiresLogin: true, onSuccess, onError);

    public void ChangeDisplayName(string displayName, Action<PlayerProfile> onSuccess, Action<ApiError>? onError = null) =>
        Send("PUT", "/api/players/me/display-name", new ChangeDisplayNameRequest { displayName = displayName },
            requiresLogin: true, onSuccess, onError);

    public void ClaimDailyReward(Action<PlayerProfile> onSuccess, Action<ApiError>? onError = null) =>
        Send("POST", "/api/players/me/daily-reward", null, requiresLogin: true, onSuccess, onError);

    // ===== Túi đồ của người chơi đang đăng nhập =====

    public void GetInventory(Action<InventoryResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("GET", "/api/players/me/inventory", null, requiresLogin: true, onSuccess, onError);

    public void UseItem(string itemId, Action<InventoryResponse> onSuccess, Action<ApiError>? onError = null) =>
        // EscapeURL: mã hoá ký tự đặc biệt (dấu cách, /, ?...) để không làm hỏng đường dẫn.
        Send("POST", $"/api/players/me/inventory/{UnityWebRequest.EscapeURL(itemId)}/use", null,
            requiresLogin: true, onSuccess, onError);

    // ===== Phần dùng chung =====

    /// <summary>
    /// Gửi một request, đợi kết quả, rồi gọi onSuccess (kèm JSON đã chuyển thành object kiểu T) hoặc onError.
    /// async void: hàm chạy "ở nền", người gọi không cần chờ; kết quả được báo qua callback.
    /// requiresLogin: API cần token. Chưa đăng nhập thì báo lỗi 401 luôn, không gửi request vô ích lên server.
    /// </summary>
    private async void Send<T>(string method, string path, object? body, bool requiresLogin,
        Action<T> onSuccess, Action<ApiError>? onError)
    {
        T data = default!;
        ApiError? error = null;

        if (requiresLogin && AccessToken == null)
        {
            Report(new ApiError(401, "Not logged in.", ""));
            return;
        }

        using (var request = new UnityWebRequest(_baseUrl + path, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = 10; // giây: server không trả lời thì bỏ cuộc, không treo mãi

            if (requiresLogin)
            {
                // Header chuẩn để gửi JWT: chữ "Bearer", một dấu cách, rồi tới token.
                request.SetRequestHeader("Authorization", "Bearer " + AccessToken);
            }

            if (body != null)
            {
                byte[] json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));
                request.uploadHandler = new UploadHandlerRaw(json);
                request.SetRequestHeader("Content-Type", "application/json");
            }

            await SendWebRequestAsync(request);

            string text = request.downloadHandler.text;
            switch (request.result)
            {
                case UnityWebRequest.Result.Success: // mọi status 2xx: 200, 201...
                    try
                    {
                        data = JsonUtility.FromJson<T>(text);
                    }
                    catch (ArgumentException)
                    {
                        error = new ApiError(request.responseCode, "Invalid JSON from server.", text);
                    }
                    break;

                case UnityWebRequest.Result.ProtocolError: // server có trả lời, nhưng là lỗi 4xx/5xx
                    if (request.responseCode == 401 && requiresLogin)
                    {
                        // Token hết hạn hoặc không hợp lệ: bỏ token đi, người chơi phải đăng nhập lại.
                        // Lý do cụ thể nằm trong header WWW-Authenticate, ví dụ: The token expired at ...
                        AccessToken = null;
                        error = new ApiError(401, "Session expired. Please log in again.",
                            request.GetResponseHeader("WWW-Authenticate") ?? "");
                    }
                    else
                    {
                        error = new ApiError(request.responseCode, ReadProblemMessage(text, request.error), text);
                    }
                    break;

                default: // không tới được server: chưa bật server, sai cổng, sai giao thức, hết thời gian chờ...
                    error = new ApiError(0, request.error, text);
                    break;
            }
        }

        // Gọi callback SAU khi đã xử lý xong request. Nếu code trong callback của bạn bị lỗi,
        // lỗi đó hiện thẳng ra Console, không bị lẫn thành "lỗi mạng".
        if (error == null)
        {
            onSuccess(data);
        }
        else
        {
            Report(error);
        }

        void Report(ApiError apiError)
        {
            if (onError != null)
            {
                onError(apiError);
            }
            else
            {
                Debug.LogWarning($"[ApiClient] {method} {path} failed ({apiError.StatusCode}): {apiError.Message}");
            }
        }
    }

    /// <summary>
    /// UnityWebRequest (Unity 2022) chưa await trực tiếp được. TaskCompletionSource biến
    /// sự kiện "completed" thành một Task để dùng với await.
    /// </summary>
    private static Task SendWebRequestAsync(UnityWebRequest request)
    {
        var completion = new TaskCompletionSource<bool>();
        request.SendWebRequest().completed += _ => completion.TrySetResult(true);
        return completion.Task;
    }

    /// <summary>Lấy "title" và "detail" từ ProblemDetails. Body không phải JSON thì dùng thông báo lỗi của Unity.</summary>
    private static string ReadProblemMessage(string body, string fallback)
    {
        try
        {
            ProblemDetails? problem = JsonUtility.FromJson<ProblemDetails>(body);
            if (problem != null && !string.IsNullOrEmpty(problem.title))
            {
                return string.IsNullOrEmpty(problem.detail) ? problem.title : $"{problem.title} ({problem.detail})";
            }
        }
        catch (ArgumentException)
        {
            // Body rỗng hoặc không phải JSON (ví dụ lỗi 405 không có body).
        }

        return fallback;
    }
}
