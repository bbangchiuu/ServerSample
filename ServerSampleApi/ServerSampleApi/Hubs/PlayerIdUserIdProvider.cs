using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ServerSampleApi.Hubs
{
    /// <summary>
    /// Cho SignalR biết mỗi kết nối thuộc về NGƯỜI CHƠI nào, để Clients.User(playerId) gửi được cho đúng người.
    ///
    /// Một người chơi có thể có NHIỀU kết nối cùng lúc (điện thoại + máy tính, hai tab trình duyệt...).
    /// Clients.User(playerId) gửi cho TẤT CẢ kết nối của người đó, còn Clients.Client(connectionId) chỉ gửi cho một kết nối.
    ///
    /// Vì sao phải tự viết: mặc định SignalR lấy user id từ claim "nameidentifier" (tên dài
    /// http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier). Mình đặt MapInboundClaims = false
    /// nên claim vẫn giữ tên "sub" như trong token, và cách mặc định sẽ không tìm thấy gì: Clients.User(...)
    /// âm thầm không gửi cho ai cả, không báo lỗi.
    /// </summary>
    public class PlayerIdUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection)
        {
            // Giống User.GetPlayerId() trong controller: "sub" chính là playerId.
            return connection.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        }
    }
}
