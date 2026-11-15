using ServerSampleApi.Services;

namespace ServerSampleApi.UnitTests;

/// <summary>
/// Unit test cho LikePattern: chữ người chơi gõ vào → pattern an toàn cho ILIKE.
/// LikePattern là "internal": project này thấy được nhờ InternalsVisibleTo trong ServerSampleApi.csproj.
///
/// Integration test (ClanApiTests) chỉ thử "%" qua HTTP. Ở đây thử hết các trường hợp khó trong vài mili giây,
/// đặc biệt là THỨ TỰ thay thế (xem Escape_BackslashBeforePercent...).
/// </summary>
public class LikePatternTests
{
    // Lưu ý cách viết chuỗi C#: "\\" trong code là MỘT dấu "\" thật. Dùng @"..." (verbatim string) để dễ đọc hơn:
    // @"\%" là đúng hai ký tự: dấu "\" và dấu "%".
    [Theory]
    [InlineData("dragon", "dragon")]          // chữ thường: giữ nguyên
    [InlineData("", "")]                      // rỗng: vẫn rỗng
    [InlineData("100%", @"100\%")]            // %: thành chữ "%" chứ không phải "chuỗi bất kỳ"
    [InlineData("a_b", @"a\_b")]              // _: thành chữ "_" chứ không phải "một ký tự bất kỳ"
    [InlineData(@"a\b", @"a\\b")]             // \ : chính ký tự escape cũng phải escape
    [InlineData("%%%", @"\%\%\%")]            // gõ toàn "%" (trước đây khớp với MỌI tên)
    [InlineData("Rồng_Đỏ", @"Rồng\_Đỏ")]      // tiếng Việt: chỉ ký tự đặc biệt bị đổi
    public void Escape_AddsBackslashBeforeSpecialCharacters(string input, string expected)
    {
        Assert.Equal(expected, LikePattern.Escape(input));
    }

    [Fact]
    public void Escape_BackslashBeforePercent_EscapesBothSeparately()
    {
        // Người chơi gõ hai ký tự: \ rồi %. Kết quả đúng: "\\" (chữ "\") + "\%" (chữ "%").
        // Nếu code thay "%" TRƯỚC "\", kết quả sai sẽ là @"\\\\%" hoặc @"\\%": test này bắt được lỗi thứ tự đó.
        Assert.Equal(@"\\\%", LikePattern.Escape(@"\%"));
    }

    [Fact]
    public void Contains_WrapsEscapedTextWithPercent()
    {
        Assert.Equal(@"%50\%%", LikePattern.Contains("50%"));
    }
}
