using ServerSampleApi.Services;

namespace ServerSampleApi.UnitTests;

/// <summary>
/// Unit test cho ServiceResult / ServiceResult&lt;T&gt;: chỉ là vài class C# thuần, không cần server hay database.
///
/// Mỗi test theo mẫu Arrange - Act - Assert (AAA):
///  - Arrange: chuẩn bị dữ liệu;
///  - Act:     làm ĐÚNG MỘT việc cần thử;
///  - Assert:  kiểm tra kết quả.
/// </summary>
public class ServiceResultTests
{
    private static readonly ServiceError ClanFull = new(ServiceErrorKind.Conflict, "Clan is full");

    [Fact]
    public void Success_IsSuccess_AndHasNoError()
    {
        // Act
        ServiceResult result = ServiceResult.Success();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ErrorConvertedImplicitly_IsFailure_WithThatError()
    {
        // Act: implicit operator, giống "return ClanErrors.NotInClan;" trong ClanService.
        ServiceResult result = ClanFull;

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Same(ClanFull, result.Error); // Same: đúng CÙNG một object, không chỉ "bằng nhau"
    }

    [Fact]
    public void ValueConvertedImplicitly_IsSuccess_WithThatValue()
    {
        ServiceResult<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void GenericResult_FromError_IsFailure()
    {
        ServiceResult<int> result = ClanFull;

        Assert.False(result.IsSuccess);
        Assert.Same(ClanFull, result.Error);
    }

    [Fact]
    public void ReadingValueOfFailedResult_Throws()
    {
        ServiceResult<int> result = ClanFull;

        // Đọc Value khi thất bại là lỗi LẬP TRÌNH (quên kiểm tra IsSuccess): phải ném exception ngay,
        // không được lặng lẽ trả về 0 rồi để bug chạy tiếp.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Contains("Clan is full", exception.Message);
    }

    [Fact]
    public void GenericResult_CanBeUsedWhereNonGenericIsExpected()
    {
        // ServiceResult<T> kế thừa ServiceResult: code chỉ cần biết thành công/thất bại thì không cần biết T.
        ServiceResult<string> generic = "hello";

        ServiceResult result = generic;

        Assert.True(result.IsSuccess);
    }

    // [Theory] + [InlineData]: CÙNG một test, chạy với nhiều bộ dữ liệu. Ở đây thử đúng cách service viết code:
    // một hàm trả về ServiceResult<int>, trong đó "return giá_trị;" hoặc "return lỗi;".
    [Theory]
    [InlineData(10, 2, true, 5)]
    [InlineData(7, 0, false, 0)]
    public void ServiceStyleMethod_ReturnsValueOrError(int a, int b, bool expectedSuccess, int expectedValue)
    {
        ServiceResult<int> result = Divide(a, b);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (expectedSuccess)
        {
            Assert.Equal(expectedValue, result.Value);
        }
        else
        {
            Assert.Equal(ServiceErrorKind.Validation, result.Error!.Kind);
        }
    }

    private static ServiceResult<int> Divide(int a, int b)
    {
        if (b == 0)
        {
            return new ServiceError(ServiceErrorKind.Validation, "Cannot divide by zero", Field: "b");
        }
        return a / b;
    }
}
