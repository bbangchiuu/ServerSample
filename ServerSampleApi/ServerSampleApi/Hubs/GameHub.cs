using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Services;
using StackExchange.Redis;

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

        /// <summary>Đánh dấu kết nối này ĐÃ được cộng vào bộ đếm online, để lúc ngắt kết nối chỉ trừ khi đã cộng.</summary>
        private const string CountedOnlineKey = "CountedOnline";

        private readonly GameDbContext _db;
        private readonly PresenceService _presence;
        private readonly ILogger<GameHub> _logger;

        public GameHub(GameDbContext db, PresenceService presence, ILogger<GameHub> logger)
        {
            _db = db;
            _presence = presence;
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

            try
            {
                bool cameOnline = await _presence.AddConnectionAsync(playerId);
                Context.Items[CountedOnlineKey] = true;
                if (cameOnline)
                {
                    // Clients.Others: mọi kết nối TRỪ kết nối vừa mở (chính người này không cần được báo "bạn vừa online").
                    await Clients.Others.PlayerOnline(new OnlinePlayer { PlayerId = playerId, DisplayName = displayName });
                }
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // Redis lỗi thì danh sách online sai một chút, nhưng chat vẫn phải dùng được: chỉ ghi cảnh báo.
                _logger.LogWarning(ex, "Could not update presence for player {PlayerId}", playerId);
            }

            await base.OnConnectedAsync();
        }

        /// <summary>
        /// Chạy khi kết nối đóng: client tự ngắt, mất mạng, tắt game...
        /// Chạy CẢ khi OnConnectedAsync đã Abort giữa chừng, nên chỉ trừ bộ đếm nếu lúc kết nối đã cộng:
        /// trừ khi chưa cộng thì bộ đếm của người đó bị lệch, có thể thành âm.
        /// </summary>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);

            if (Context.Items.ContainsKey(CountedOnlineKey))
            {
                Guid playerId = Context.User!.GetPlayerId();
                try
                {
                    bool wentOffline = await _presence.RemoveConnectionAsync(playerId);
                    if (wentOffline)
                    {
                        await Clients.Others.PlayerOffline(new OnlinePlayer
                        {
                            PlayerId = playerId,
                            DisplayName = (string)Context.Items[DisplayNameKey]!,
                        });
                    }
                }
                catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
                {
                    _logger.LogWarning(ex, "Could not update presence for player {PlayerId}", playerId);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Client gọi: lấy danh sách người đang online (gọi một lần sau khi kết nối, rồi cập nhật bằng
        /// PlayerOnline/PlayerOffline). Hàm của hub CÓ GIÁ TRỊ TRẢ VỀ: kết quả nằm trong gói Completion (type 3),
        /// trường "result", giống response của HTTP nhưng đi qua kết nối WebSocket sẵn có.
        /// </summary>
        public async Task<OnlinePlayersResponse> GetOnlinePlayers()
        {
            List<Guid> playerIds = await _presence.GetOnlinePlayerIdsAsync();

            // Tên hiển thị đọc từ PostgreSQL bằng MỘT câu SQL (WHERE "Id" IN (...)), giống bảng xếp hạng.
            List<OnlinePlayer> players = await _db.Players
                .Where(p => playerIds.Contains(p.Id))
                .OrderBy(p => p.DisplayName)
                .Select(p => new OnlinePlayer { PlayerId = p.Id, DisplayName = p.DisplayName })
                .ToListAsync();

            return new OnlinePlayersResponse { Players = players };
        }

        /// <summary>Client gọi: gửi tin nhắn chat cho TẤT CẢ người chơi đang kết nối (kể cả chính người gửi).</summary>
        public async Task SendChatMessage(string text)
        {
            ChatMessage message = CreateChatMessage(text);

            // Clients.All: mọi kết nối đang mở. SignalR gửi cho từng kết nối, server không phải tự lặp.
            await Clients.All.ReceiveChatMessage(message);
        }

        /// <summary>
        /// Client gọi: gửi tin nhắn cho các thành viên trong clan của mình (kể cả chính mình).
        ///
        /// Danh sách người nhận đọc từ DATABASE mỗi lần gửi, rồi gửi bằng Clients.Users(...) (mọi kết nối của
        /// những người đó). Không dùng SignalR Groups vì nhóm gắn với từng KẾT NỐI: vào/rời clan qua HTTP thì server
        /// không biết người đó đang mở những kết nối nào để thêm/bớt khỏi nhóm. Đọc từ database thì luôn đúng
        /// ngay sau khi vào/rời clan, trên mọi thiết bị, và chạy được qua backplane khi có nhiều server.
        /// </summary>
        public async Task SendClanChatMessage(string text)
        {
            ChatMessage message = CreateChatMessage(text);

            // Mọi thành viên cùng clan với người gửi, bằng MỘT câu SQL:
            //   SELECT m."PlayerId" FROM "ClanMembers" m
            //   WHERE EXISTS (SELECT 1 FROM "ClanMembers" me WHERE me."PlayerId" = @sender AND me."ClanId" = m."ClanId")
            // Người gửi không ở clan nào thì danh sách rỗng.
            Guid senderId = message.PlayerId;
            List<string> memberIds = await _db.ClanMembers
                .Where(m => _db.ClanMembers.Any(me => me.PlayerId == senderId && me.ClanId == m.ClanId))
                .Select(m => m.PlayerId.ToString())
                .ToListAsync();

            if (memberIds.Count == 0)
            {
                throw new HubException("You are not in a clan.");
            }

            await Clients.Users(memberIds).ReceiveClanChatMessage(message);
        }

        /// <summary>
        /// Kiểm tra nội dung rồi tạo tin nhắn, dùng chung cho chat thế giới và chat clan.
        /// Người gửi lấy từ token, không nhận từ client: client không giả làm người khác được.
        /// </summary>
        private ChatMessage CreateChatMessage(string? text)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0 || text.Length > MaxChatMessageLength)
            {
                // HubException: lỗi được gửi nguyên văn cho client. Lỗi kiểu khác thì client chỉ nhận
                // thông báo chung chung, để không lộ chi tiết bên trong server.
                throw new HubException($"Message must be 1-{MaxChatMessageLength} characters.");
            }

            return new ChatMessage
            {
                PlayerId = Context.User!.GetPlayerId(),
                DisplayName = (string)Context.Items[DisplayNameKey]!,
                Text = text,
                SentAt = DateTime.UtcNow,
            };
        }
    }
}
