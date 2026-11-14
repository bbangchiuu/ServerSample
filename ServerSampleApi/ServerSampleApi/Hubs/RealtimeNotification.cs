using StackExchange.Redis;

namespace ServerSampleApi.Hubs
{
    /// <summary>
    /// Gửi thông báo realtime (SignalR) theo kiểu "cố gắng hết sức" (best effort), dùng SAU KHI dữ liệu đã lưu xong.
    ///
    /// Vì sao cần: mọi lệnh gửi (Clients.User, Clients.Users, Clients.All...) đều đi qua Redis backplane (PUBLISH).
    /// Redis sập thì lệnh gửi ném RedisConnectionException / RedisTimeoutException. Nếu để lỗi lọt ra ngoài,
    /// GlobalExceptionHandler trả 503 cho client, dù vàng/clan... ĐÃ được lưu chắc chắn trong PostgreSQL.
    /// Client tưởng thất bại, bấm lại, rồi bị báo "hôm nay đã nhận" hay "bạn đã ở trong clan": rất khó hiểu.
    ///
    /// Thông báo realtime chỉ là "tin báo cho nhanh", không phải nguồn dữ liệu chính: lỡ mất một tin thì
    /// lần sau client tải lại (GET profile, GET clan...) vẫn thấy đúng dữ liệu. Nên ở đây chỉ ghi log cảnh báo.
    ///
    /// Chỉ bắt lỗi của Redis. Lỗi khác (bug trong code...) vẫn ném ra như bình thường, để không bị giấu đi.
    /// Lưu ý: Redis sập thì mỗi lệnh gửi vẫn phải chờ hết thời gian chờ của thư viện (asyncTimeout, mặc định 5 giây)
    /// rồi mới báo lỗi, nên request chậm hơn nhưng vẫn trả đúng kết quả.
    /// </summary>
    public static class RealtimeNotification
    {
        /// <summary>
        /// Chạy send(). Lỗi do Redis thì ghi log cảnh báo và bỏ qua.
        /// Dùng: await RealtimeNotification.TrySendAsync(_logger, "ProfileChanged", () => _hub.Clients.User(id).ProfileChanged(p));
        /// </summary>
        public static async Task TrySendAsync(ILogger logger, string notificationName, Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                // RedisConnectionException là lớp con của RedisException, nên cũng được bắt ở đây.
                logger.LogWarning(ex,
                    "Could not send realtime notification {Notification}. The data is saved; clients will see it when they reload.",
                    notificationName);
            }
        }
    }
}
