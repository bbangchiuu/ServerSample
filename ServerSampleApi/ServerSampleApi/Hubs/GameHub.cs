using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;

namespace ServerSampleApi.Hubs
{
    /// <summary>
    /// Kênh realtime của game (SignalR), địa chỉ /hubs/game.
    ///
    /// Khác HTTP API: với API, client hỏi thì server mới trả lời, xong là đóng. Với hub, client mở MỘT kết nối
    /// (thường là WebSocket) và giữ nó suốt lúc chơi. Qua kết nối đó:
    ///  - client gọi hàm của server (client → server): ví dụ SendChatMessage bên dưới;
    ///  - server gọi hàm của client BẤT CỨ LÚC NÀO (server → client): ví dụ ReceiveChatMessage trong IGameClient.
    /// Giống TCP server ở giai đoạn 1, nhưng SignalR lo hết: tách gói tin, chuyển JSON, tự chọn WebSocket hay cách khác,
    /// kiểm tra đăng nhập, và danh sách các kết nối đang mở để gửi cho "tất cả" hay cho "một người".
    ///
    /// [Authorize]: phải có access token hợp lệ mới kết nối được, giống các API /me.
    /// Mỗi lần client gọi một hàm, ASP.NET Core tạo một GameHub MỚI (giống controller), nên KHÔNG lưu dữ liệu
    /// trong field của class. Dữ liệu cần giữ suốt kết nối thì để trong Context.Items.
    /// </summary>
    [Authorize]
    public class GameHub : Hub<IGameClient>
    {
        public const string Path = "/hubs/game";

        public const int MaxChatMessageLength = 200;

        private const string DisplayNameKey = "DisplayName";

        private readonly GameDbContext _db;
        private readonly ILogger<GameHub> _logger;

        public GameHub(GameDbContext db, ILogger<GameHub> logger)
        {
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Chạy MỘT lần khi client vừa kết nối xong. Đọc tên hiển thị một lần rồi giữ trong Context.Items,
        /// để mỗi tin nhắn không phải hỏi database. Đổi tên giữa chừng thì phải kết nối lại mới thấy tên mới.
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            Guid playerId = Context.User!.GetPlayerId();
            string? displayName = await _db.Players
                .Where(p => p.Id == playerId)
                .Select(p => p.DisplayName)
                .FirstOrDefaultAsync();

            if (displayName == null)
            {
                // Token hợp lệ nhưng người chơi không còn trong database (tài khoản đã bị xoá): đóng kết nối.
                Context.Abort();
                return;
            }

            Context.Items[DisplayNameKey] = displayName;
            _logger.LogInformation("Player {PlayerId} connected ({ConnectionId})", playerId, Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        /// <summary>Chạy khi kết nối đóng: client tự ngắt, mất mạng, tắt game...</summary>
        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client gọi: gửi tin nhắn chat cho TẤT CẢ người chơi đang kết nối (kể cả chính người gửi).
        /// Người gửi lấy từ token, không nhận từ client: client không giả làm người khác được.
        /// </summary>
        public async Task SendChatMessage(string text)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > MaxChatMessageLength)
            {
                // HubException: lỗi được gửi nguyên văn cho client. Lỗi kiểu khác thì client chỉ nhận
                // thông báo chung chung, để không lộ chi tiết bên trong server.
                throw new HubException($"Message must be 1-{MaxChatMessageLength} characters.");
            }

            var message = new ChatMessage
            {
                PlayerId = Context.User!.GetPlayerId(),
                DisplayName = (string)Context.Items[DisplayNameKey]!,
                Text = text,
                SentAt = DateTime.UtcNow,
            };

            // Clients.All: mọi kết nối đang mở. SignalR gửi cho từng kết nối, server không phải tự lặp.
            await Clients.All.ReceiveChatMessage(message);
        }
    }
}
