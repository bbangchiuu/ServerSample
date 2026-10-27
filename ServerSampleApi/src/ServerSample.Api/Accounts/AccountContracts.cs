using System.ComponentModel.DataAnnotations;

namespace ServerSample.Api.Accounts;

// "Contract" = hình dạng JSON mà client gửi lên / nhận về. Tách riêng khỏi class dữ liệu (Account, Player)
// để không bao giờ vô tình trả PasswordHash ra ngoài, và đổi cấu trúc dữ liệu bên trong không làm vỡ client.
// [property: ...] để attribute gắn vào property (Validator kiểm tra property, không kiểm tra tham số constructor).

public sealed record RegisterRequest(
    [property: Required]
    [property: StringLength(20, MinimumLength = 3)]
    [property: RegularExpression("^[a-zA-Z0-9_]+$", ErrorMessage = "Username may only contain letters, digits and '_'.")]
    string Username,

    [property: Required]
    [property: StringLength(64, MinimumLength = 6)]
    string Password);

public sealed record LoginRequest(
    [property: Required] string Username,
    [property: Required] string Password);

public sealed record AccountResponse(Guid PlayerId, string Username);
