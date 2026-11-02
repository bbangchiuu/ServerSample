using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ServerSampleApi.Auth
{
    /// <summary>
    /// Hàm mở rộng (extension method) cho ClaimsPrincipal, tức "User" trong controller.
    /// Nhờ chữ "this" ở tham số đầu, gọi được như hàm có sẵn: User.GetPlayerId()
    /// </summary>
    public static class ClaimsPrincipalExtensions
    {
        /// <summary>
        /// playerId của người đang gọi API, lấy từ claim "sub" trong token.
        ///
        /// Đây là điểm mấu chốt của bước này: playerId đến từ token đã được server KIỂM TRA CHỮ KÝ,
        /// không phải từ URL hay body do client tự gõ. Client muốn đổi thành playerId của người khác thì
        /// phải sửa token, mà sửa token thì chữ ký sai, request bị từ chối ngay.
        ///
        /// Chỉ gọi trong API có [Authorize]: ở đó chắc chắn đã có token hợp lệ, nên chắc chắn có "sub".
        /// </summary>
        public static Guid GetPlayerId(this ClaimsPrincipal user)
        {
            string? subject = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (subject == null || !Guid.TryParse(subject, out Guid playerId))
            {
                // Chỉ xảy ra khi lập trình sai (quên [Authorize]), không phải do người chơi.
                throw new InvalidOperationException("The current user has no valid 'sub' claim. Is [Authorize] missing?");
            }

            return playerId;
        }
    }
}
