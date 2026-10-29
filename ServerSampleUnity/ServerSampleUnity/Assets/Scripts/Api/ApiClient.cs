#nullable enable
using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Kết quả một lần gọi API: thành công kèm dữ liệu, hoặc thất bại kèm thông báo lỗi.</summary>
public class ApiResult<T>
{
    public bool IsSuccess { get; private set; }

    /// <summary>HTTP status code (200, 201, 404, 409...). 0 nếu không kết nối được tới server.</summary>
    public long StatusCode { get; private set; }

    public T? Data { get; private set; }

    public string ErrorMessage { get; private set; } = "";

    /// <summary>Nội dung gốc server trả về, để xem chi tiết khi có lỗi (ví dụ danh sách lỗi validation).</summary>
    public string RawBody { get; private set; } = "";

    public static ApiResult<T> Success(long statusCode, T data, string rawBody) =>
        new ApiResult<T> { IsSuccess = true, StatusCode = statusCode, Data = data, RawBody = rawBody };

    public static ApiResult<T> Failure(long statusCode, string errorMessage, string rawBody) =>
        new ApiResult<T> { IsSuccess = false, StatusCode = statusCode, ErrorMessage = errorMessage, RawBody = rawBody };
}

/// <summary>
/// Gọi HTTP API của ServerSampleApi bằng UnityWebRequest.
///
/// Mọi hàm là async và phải được gọi từ MAIN THREAD (ví dụ từ MonoBehaviour): UnityWebRequest
/// chỉ dùng được trên main thread. Sau mỗi await, Unity tự đưa code quay về main thread,
/// nên dùng kết quả để cập nhật giao diện ngay là an toàn.
/// </summary>
public class ApiClient
{
    private readonly string _baseUrl;

    public ApiClient(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    // ===== Tài khoản =====

    public Task<ApiResult<AccountResponse>> RegisterAsync(string username, string password) =>
        SendAsync<AccountResponse>("POST", "/api/accounts/register", new AccountRequest { username = username, password = password });

    public Task<ApiResult<AccountResponse>> LoginAsync(string username, string password) =>
        SendAsync<AccountResponse>("POST", "/api/accounts/login", new AccountRequest { username = username, password = password });

    // ===== Người chơi =====

    public Task<ApiResult<PlayerProfile>> GetProfileAsync(string playerId) =>
        SendAsync<PlayerProfile>("GET", $"/api/players/{playerId}", null);

    public Task<ApiResult<PlayerProfile>> ChangeDisplayNameAsync(string playerId, string displayName) =>
        SendAsync<PlayerProfile>("PUT", $"/api/players/{playerId}/display-name", new ChangeDisplayNameRequest { displayName = displayName });

    public Task<ApiResult<PlayerProfile>> ClaimDailyRewardAsync(string playerId) =>
        SendAsync<PlayerProfile>("POST", $"/api/players/{playerId}/daily-reward", null);

    // ===== Túi đồ =====

    public Task<ApiResult<InventoryResponse>> GetInventoryAsync(string playerId) =>
        SendAsync<InventoryResponse>("GET", $"/api/players/{playerId}/inventory", null);

    public Task<ApiResult<InventoryResponse>> UseItemAsync(string playerId, string itemId) =>
        // EscapeURL: mã hoá ký tự đặc biệt (dấu cách, /, ?...) để không làm hỏng đường dẫn.
        SendAsync<InventoryResponse>("POST", $"/api/players/{playerId}/inventory/{UnityWebRequest.EscapeURL(itemId)}/use", null);

    // ===== Phần dùng chung =====

    /// <summary>Gửi một request, đợi kết quả, rồi chuyển JSON trả về thành object kiểu T.</summary>
    private async Task<ApiResult<T>> SendAsync<T>(string method, string path, object? body)
    {
        using var request = new UnityWebRequest(_baseUrl + path, method);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Accept", "application/json");
        request.timeout = 10; // giây: server không trả lời thì bỏ cuộc, không treo mãi

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
                return ApiResult<T>.Success(request.responseCode, JsonUtility.FromJson<T>(text), text);

            case UnityWebRequest.Result.ProtocolError: // server có trả lời, nhưng là lỗi 4xx/5xx
                return ApiResult<T>.Failure(request.responseCode, ReadProblemMessage(text, request.error), text);

            default: // không tới được server: chưa bật server, sai cổng, bị chặn HTTP, hết thời gian chờ...
                return ApiResult<T>.Failure(0, request.error, text);
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
