using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ServerSampleApi.Auth;
using ServerSampleApi.Models;

namespace ServerSampleApi.UnitTests;

/// <summary>
/// Unit test cho TokenService: tạo JWT với đồng hồ GIẢ, rồi đọc ngược token ra để kiểm tra từng claim.
/// Không cần server: TokenService chỉ cần JwtOptions và một TimeProvider, cả hai đều tự "new" được ở đây.
/// </summary>
public class TokenServiceTests
{
    // Giờ cố định, tròn giây: trong JWT, iat/nbf/exp chỉ lưu tới GIÂY (số giây kể từ 1970), không có mili giây.
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string SigningKey = "unit-test-signing-key-0123456789-abcdefghij";

    private static readonly JwtOptions JwtSettings = new()
    {
        Issuer = "ServerSampleApi",
        Audience = "ServerSampleUnity",
        SigningKey = SigningKey,
        AccessTokenMinutes = 15,
    };

    private static readonly AccountModel Account = new() { Id = Guid.NewGuid(), Username = "bang" };

    // Options.Create: bọc object JwtOptions thành IOptions<JwtOptions>, đúng kiểu constructor cần (thay cho DI).
    private static TokenService CreateService(TimeProvider timeProvider) =>
        new(Options.Create(JwtSettings), timeProvider);

    [Fact]
    public void AccessToken_ExpiresAfterConfiguredMinutes_FromNow()
    {
        TokenService service = CreateService(new FixedTimeProvider(Now));

        AccessToken token = service.CreateAccessToken(Account);

        // Nhờ đồng hồ giả, so được CHÍNH XÁC. Với DateTime.UtcNow thì chỉ kiểm được kiểu "khoảng 15 phút nữa".
        DateTime expected = Now.UtcDateTime.AddMinutes(15);
        Assert.Equal(expected, token.ExpiresAt);

        // Đọc ngược token (không kiểm tra chữ ký, chỉ giải Base64Url phần payload).
        var jwt = new JsonWebToken(token.Token);
        Assert.Equal(Now.UtcDateTime, jwt.IssuedAt); // "iat"
        Assert.Equal(Now.UtcDateTime, jwt.ValidFrom); // "nbf"
        Assert.Equal(expected, jwt.ValidTo);         // "exp"
    }

    [Fact]
    public void AccessToken_UsesTimeAtTheMomentItIsCreated()
    {
        var clock = new FixedTimeProvider(Now);
        TokenService service = CreateService(clock);

        AccessToken first = service.CreateAccessToken(Account);
        clock.Advance(TimeSpan.FromHours(2)); // "2 tiếng sau" mà không phải chờ 2 tiếng
        AccessToken second = service.CreateAccessToken(Account);

        Assert.Equal(TimeSpan.FromHours(2), second.ExpiresAt - first.ExpiresAt);
    }

    [Fact]
    public void AccessToken_ContainsPlayerIdUsernameIssuerAndAudience()
    {
        TokenService service = CreateService(new FixedTimeProvider(Now));

        var jwt = new JsonWebToken(service.CreateAccessToken(Account).Token);

        Assert.Equal(Account.Id.ToString(), jwt.Subject);                                     // "sub" = playerId
        Assert.Equal("bang", jwt.GetClaim(JwtRegisteredClaimNames.UniqueName).Value);           // "unique_name"
        Assert.Equal("ServerSampleApi", jwt.Issuer);                                          // "iss"
        Assert.Equal(["ServerSampleUnity"], jwt.Audiences);                                   // "aud"
        Assert.Equal(SecurityAlgorithms.HmacSha256, jwt.Alg);                                 // header "alg" = HS256
    }

    [Fact]
    public void EachToken_HasItsOwnId()
    {
        TokenService service = CreateService(new FixedTimeProvider(Now));

        var first = new JsonWebToken(service.CreateAccessToken(Account).Token);
        var second = new JsonWebToken(service.CreateAccessToken(Account).Token);

        // Cùng người, cùng giây, nhưng "jti" khác nhau: sau này thu hồi được TỪNG token.
        Assert.NotEqual(first.Id, second.Id);
    }

    [Theory]
    [InlineData(SigningKey, true)]                                         // đúng khoá: chữ ký hợp lệ
    [InlineData("another-key-that-is-also-long-enough-0123456789", false)] // khoá khác: bị từ chối
    public async Task Signature_IsValidOnlyWithTheSameKey(string validationKey, bool expectedValid)
    {
        TokenService service = CreateService(new FixedTimeProvider(Now));
        string token = service.CreateAccessToken(Account).Token;

        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = JwtSettings.Issuer,
            ValidAudience = JwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(validationKey)),
            // Token được tạo "năm 2030" theo đồng hồ giả, còn bộ kiểm tra dùng giờ THẬT: nếu kiểm tra hạn dùng,
            // token sẽ bị coi là "chưa tới lúc dùng". Test này chỉ thử chữ ký nên tắt phần đó; hạn dùng đã có test riêng ở trên.
            ValidateLifetime = false,
        });

        Assert.Equal(expectedValid, result.IsValid);
    }
}
