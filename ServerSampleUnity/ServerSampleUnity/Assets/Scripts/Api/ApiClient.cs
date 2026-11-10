#nullable enable
using System;
using System.Globalization;
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
/// Đăng nhập: Register/Login thành công thì ApiClient TỰ GIỮ hai token:
///  - Access token (JWT, sống 15 phút): gửi kèm "Authorization: Bearer &lt;token&gt;" cho các API của người chơi.
///    Server lấy playerId từ token, nên các hàm bên dưới không cần (và không nhận) playerId.
///  - Refresh token (sống 30 ngày): access token hết hạn thì ApiClient TỰ đổi refresh token lấy cặp token mới,
///    rồi gửi lại request vừa bị từ chối. Người gọi không thấy gì, chỉ thấy request thành công.
/// Chỉ khi refresh token cũng không dùng được nữa thì người gọi mới nhận lỗi 401 "Session expired".
/// Mỗi ApiClient giữ token của MỘT người chơi. Muốn thử nhiều người chơi cùng lúc thì tạo nhiều ApiClient.
/// </summary>
public class ApiClient
{
    private readonly string _baseUrl;

    /// <summary>Refresh token hiện tại. Mỗi lần làm mới, server phát token mới và token cũ không dùng được nữa.</summary>
    private string? _refreshToken;

    /// <summary>Lần làm mới token đang chạy dở, để các request cùng gặp 401 chờ chung một lần làm mới.</summary>
    private Task<ApiError?>? _refreshTask;

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

    /// <summary>Thời điểm access token hết hạn (UTC), đọc từ accessTokenExpiresAt server trả về.</summary>
    private DateTime _accessTokenExpiresAt;

    /// <summary>Token còn hạn ít hơn khoảng này thì coi như sắp hết, làm mới trước cho chắc.</summary>
    private static readonly TimeSpan AccessTokenRefreshMargin = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Trả về access token CÒN HẠN, dùng cho những chỗ cần token mà không đi qua Send (ví dụ kết nối HubClient).
    /// Token sắp hết hạn thì làm mới trước bằng refresh token. Kết quả:
    ///  - token: dùng được ngay;
    ///  - null: chưa đăng nhập, hoặc refresh token cũng hết hạn/bị thu hồi (phải đăng nhập lại);
    ///  - ném exception: không tới được server để làm mới (mất mạng...), lát nữa thử lại.
    /// </summary>
    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (AccessToken == null)
        {
            return null;
        }

        if (DateTime.UtcNow < _accessTokenExpiresAt - AccessTokenRefreshMargin)
        {
            return AccessToken;
        }

        ApiError? error = await RefreshTokensAsync();
        if (error == null)
        {
            return AccessToken;
        }
        if (error.StatusCode == 0)
        {
            throw new InvalidOperationException("Cannot refresh access token: " + error.Message);
        }
        return null; // server từ chối refresh token, ApiClient đã xoá token
    }

    /// <summary>
    /// Đăng xuất: quên token ngay trên máy, rồi báo server thu hồi refresh token.
    /// Không chờ server trả lời: lỡ mạng lỗi thì người chơi vẫn đăng xuất được, chỉ là refresh token
    /// trên server còn sống tới khi hết hạn (máy này đã quên nó nên không ai dùng nữa).
    /// </summary>
    public void Logout()
    {
        string? refreshToken = _refreshToken;
        ClearTokens();

        if (refreshToken != null)
        {
            Send<EmptyResponse>("POST", "/api/accounts/logout", new RefreshTokenRequest { refreshToken = refreshToken },
                requiresLogin: false, _ => { },
                error => Debug.LogWarning($"[ApiClient] Server logout failed ({error.StatusCode}): {error.Message}"));
        }
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
        StoreTokens(account);
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

    // ===== Clan (/api/clans) =====

    /// <summary>Tìm clan có tên chứa search (không phân biệt hoa thường). search rỗng: lấy vài clan đầu tiên theo tên.</summary>
    public void SearchClans(string search, Action<ClanListResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("GET", "/api/clans?search=" + UnityWebRequest.EscapeURL(search), null, requiresLogin: true, onSuccess, onError);

    public void CreateClan(string name, Action<ClanResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("POST", "/api/clans", new CreateClanRequest { name = name }, requiresLogin: true, onSuccess, onError);

    public void GetClan(string clanId, Action<ClanResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("GET", $"/api/clans/{clanId}", null, requiresLogin: true, onSuccess, onError);

    /// <summary>Clan của mình. Chưa ở clan nào thì server trả 404 (gọi onError với StatusCode 404).</summary>
    public void GetMyClan(Action<ClanResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("GET", "/api/clans/me", null, requiresLogin: true, onSuccess, onError);

    public void JoinClan(string clanId, Action<ClanResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("POST", $"/api/clans/{clanId}/join", null, requiresLogin: true, onSuccess, onError);

    /// <summary>Rời clan. Server trả 204 (không có dữ liệu), nên onSuccess nhận null.</summary>
    public void LeaveClan(Action<EmptyResponse> onSuccess, Action<ApiError>? onError = null) =>
        Send("POST", "/api/clans/me/leave", null, requiresLogin: true, onSuccess, onError);

    // ===== Phần dùng chung =====

    /// <summary>
    /// Gửi một request, đợi kết quả, rồi gọi onSuccess (kèm JSON đã chuyển thành object kiểu T) hoặc onError.
    /// async void: hàm chạy "ở nền", người gọi không cần chờ; kết quả được báo qua callback.
    /// requiresLogin: API cần token. Chưa đăng nhập thì báo lỗi 401 luôn, không gửi request vô ích lên server.
    /// </summary>
    private async void Send<T>(string method, string path, object? body, bool requiresLogin,
        Action<T> onSuccess, Action<ApiError>? onError)
    {
        (T data, ApiError? error) = await SendWithRefreshAsync<T>(method, path, body, requiresLogin);

        // Gọi callback SAU khi đã xử lý xong request. Nếu code trong callback của bạn bị lỗi,
        // lỗi đó hiện thẳng ra Console, không bị lẫn thành "lỗi mạng".
        if (error == null)
        {
            onSuccess(data);
        }
        else if (onError != null)
        {
            onError(error);
        }
        else
        {
            Debug.LogWarning($"[ApiClient] {method} {path} failed ({error.StatusCode}): {error.Message}");
        }
    }

    /// <summary>
    /// Gửi request. Nếu là API cần đăng nhập mà server trả 401 (access token hết hạn) thì làm mới token
    /// rồi gửi lại ĐÚNG MỘT lần. Gửi lại vẫn 401 thì thôi, không thử mãi.
    /// </summary>
    private async Task<(T data, ApiError? error)> SendWithRefreshAsync<T>(string method, string path, object? body, bool requiresLogin)
    {
        if (!requiresLogin)
        {
            return await SendOnceAsync<T>(method, path, body, accessToken: null);
        }

        if (AccessToken == null)
        {
            return (default!, new ApiError(401, "Not logged in.", ""));
        }

        string usedToken = AccessToken;
        (T data, ApiError? error) = await SendOnceAsync<T>(method, path, body, usedToken);
        if (error == null || error.StatusCode != 401)
        {
            return (data, error);
        }

        // 401. Nếu token đã đổi trong lúc request này đang chạy (một request khác vừa làm mới xong),
        // thì không cần làm mới nữa, chỉ cần gửi lại bằng token mới.
        if (AccessToken == usedToken)
        {
            ApiError? refreshError = await RefreshTokensAsync();
            if (refreshError != null)
            {
                return (default!, refreshError);
            }
        }

        if (AccessToken == null)
        {
            // Người chơi đã đăng xuất trong lúc chờ.
            return (default!, new ApiError(401, "Not logged in.", ""));
        }

        (T retryData, ApiError? retryError) = await SendOnceAsync<T>(method, path, body, AccessToken);
        if (retryError != null && retryError.StatusCode == 401)
        {
            // Token vừa làm mới mà vẫn bị từ chối: có gì đó sai hẳn, bắt đăng nhập lại.
            ClearTokens();
            return (default!, new ApiError(401, "Session expired. Please log in again.", retryError.RawBody));
        }
        return (retryData, retryError);
    }

    /// <summary>
    /// Làm mới token. Trả về null nếu thành công, hoặc lỗi nếu thất bại.
    ///
    /// Nhiều request cùng gặp 401 một lúc (ví dụ bấm B: 20 request nhận thưởng cùng lúc) thì chỉ gửi MỘT request làm mới,
    /// các request kia chờ chung kết quả của nó. Nếu mỗi request tự làm mới, cùng một refresh token sẽ bị gửi lên nhiều lần:
    /// server tưởng token bị đánh cắp và thu hồi TẤT CẢ token, người chơi bị đăng xuất.
    ///
    /// Không cần lock: mọi đoạn code ở đây đều chạy trên main thread của Unity (sau mỗi await, Unity đưa code
    /// quay về main thread), nên không có hai đoạn nào chạy song song để tranh nhau _refreshTask.
    /// </summary>
    private Task<ApiError?> RefreshTokensAsync()
    {
        if (_refreshTask == null || _refreshTask.IsCompleted)
        {
            _refreshTask = RefreshTokensCoreAsync();
        }
        return _refreshTask;
    }

    private async Task<ApiError?> RefreshTokensCoreAsync()
    {
        string? refreshToken = _refreshToken;
        if (refreshToken == null)
        {
            ClearTokens();
            return new ApiError(401, "Session expired. Please log in again.", "");
        }

        (AccountResponse account, ApiError? error) = await SendOnceAsync<AccountResponse>(
            "POST", "/api/accounts/refresh", new RefreshTokenRequest { refreshToken = refreshToken }, accessToken: null);

        if (_refreshToken != refreshToken)
        {
            // Trong lúc chờ, người chơi đã đăng xuất (hoặc đăng nhập tài khoản khác): bỏ kết quả này.
            return new ApiError(401, "Not logged in.", "");
        }

        if (error == null)
        {
            StoreTokens(account);
            Debug.Log($"[ApiClient] Access token refreshed, expires at {account.accessTokenExpiresAt} (UTC)");
            return null;
        }

        if (error.StatusCode == 0)
        {
            // Không tới được server (mất mạng...): GIỮ token, lát nữa mạng có lại thì làm mới tiếp được.
            return error;
        }

        // Server từ chối refresh token (hết hạn, đã thu hồi...): phải đăng nhập lại bằng mật khẩu.
        ClearTokens();
        return new ApiError(401, "Session expired. Please log in again.", error.RawBody);
    }

    private void StoreTokens(AccountResponse account)
    {
        AccessToken = account.accessToken;
        _refreshToken = account.refreshToken;

        // "2026-11-07T09:26:37.95Z": chữ Z nghĩa là giờ UTC. RoundtripKind giữ nguyên thông tin đó khi đọc.
        // Đọc không được thì coi như đã hết hạn: lần sau GetValidAccessTokenAsync sẽ làm mới, không sao cả.
        _accessTokenExpiresAt = DateTime.TryParse(account.accessTokenExpiresAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime expiresAt)
            ? expiresAt.ToUniversalTime()
            : DateTime.MinValue;
    }

    private void ClearTokens()
    {
        AccessToken = null;
        _refreshToken = null;
    }

    /// <summary>
    /// Gửi đúng MỘT request (không tự làm mới token). accessToken khác null thì gửi kèm header Authorization.
    /// Trả về (dữ liệu, null) nếu thành công, hoặc (default, lỗi) nếu thất bại.
    /// </summary>
    private async Task<(T data, ApiError? error)> SendOnceAsync<T>(string method, string path, object? body, string? accessToken)
    {
        using (var request = new UnityWebRequest(_baseUrl + path, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = 10; // giây: server không trả lời thì bỏ cuộc, không treo mãi

            if (accessToken != null)
            {
                // Header chuẩn để gửi JWT: chữ "Bearer", một dấu cách, rồi tới token.
                request.SetRequestHeader("Authorization", "Bearer " + accessToken);
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
                case UnityWebRequest.Result.Success: // mọi status 2xx: 200, 201, 204...
                    if (string.IsNullOrEmpty(text))
                    {
                        return (default!, null); // 204 No Content: không có dữ liệu để đọc
                    }
                    try
                    {
                        return (JsonUtility.FromJson<T>(text), null);
                    }
                    catch (ArgumentException)
                    {
                        return (default!, new ApiError(request.responseCode, "Invalid JSON from server.", text));
                    }

                case UnityWebRequest.Result.ProtocolError: // server có trả lời, nhưng là lỗi 4xx/5xx
                    // 401 do token sai/hết hạn thì body rỗng, lý do nằm trong header WWW-Authenticate,
                    // ví dụ: Bearer error="invalid_token", error_description="The token expired at ..."
                    string rawBody = string.IsNullOrEmpty(text) ? request.GetResponseHeader("WWW-Authenticate") ?? "" : text;
                    return (default!, new ApiError(request.responseCode, ReadProblemMessage(text, request.error), rawBody));

                default: // không tới được server: chưa bật server, sai cổng, sai giao thức, hết thời gian chờ...
                    return (default!, new ApiError(0, request.error, text));
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
