using System.Net.Sockets;
using Npgsql;
using ServerSampleApi.Errors;
using StackExchange.Redis;

namespace ServerSampleApi.UnitTests;

/// <summary>
/// Unit test cho GlobalExceptionHandler.IsDependencyUnavailable: exception nào thành 503 (database/Redis tạm thời
/// không dùng được, client thử lại sau), exception nào thành 500 (bug, thử lại cũng vô ích).
///
/// Integration test (ErrorHandlingTests) chỉ thử được MỘT trường hợp thật: database sập. Muốn thử Redis timeout,
/// lỗi SQL, exception bọc nhiều lớp... qua HTTP thì rất khó dựng. Ở đây chỉ cần "new" exception rồi gọi hàm.
/// </summary>
public class GlobalExceptionHandlerTests
{
    // MemberData: dữ liệu cho [Theory] lấy từ một property, dùng khi dữ liệu là object phức tạp
    // (InlineData chỉ nhận hằng số: số, chuỗi, bool...).
    public static TheoryData<string, Exception> UnavailableCases => new()
    {
        { "Npgsql không kết nối được (lỗi mạng bên trong)", new NpgsqlException("Failed to connect", new SocketException()) },
        { "EF Core bọc lỗi Npgsql", new InvalidOperationException("transient failure",
            new NpgsqlException("Failed to connect", new SocketException())) },
        { "Bọc nhiều lớp", new AggregateException(new InvalidOperationException("outer",
            new NpgsqlException("Failed to connect", new TimeoutException()))) },
        { "Redis không kết nối được",
            new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection",
                innerException: null, CommandStatus.Unknown) },
        { "Redis quá thời gian chờ",
            new RedisTimeoutException(CommandFlags.None, "Timeout performing GET", CommandStatus.Unknown) },
        { "TimeoutException chung", new TimeoutException() },
    };

    public static TheoryData<string, Exception> BugCases => new()
    {
        { "Bug trong code", new NullReferenceException() },
        { "InvalidOperationException KHÔNG bọc lỗi database", new InvalidOperationException("Sequence contains no elements") },
        // Lỗi do câu SQL / dữ liệu (ví dụ trùng unique key) KHÔNG phải "tạm thời": thử lại vẫn lỗi y như vậy.
        { "PostgreSQL báo trùng unique key", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation) },
        { "NpgsqlException không phải lỗi mạng", new NpgsqlException("Something else") },
    };

    [Theory]
    [MemberData(nameof(UnavailableCases))]
    public void DependencyUnavailable_Returns_True(string description, Exception exception)
    {
        Assert.True(GlobalExceptionHandler.IsDependencyUnavailable(exception), description);
    }

    [Theory]
    [MemberData(nameof(BugCases))]
    public void OtherErrors_Return_False(string description, Exception exception)
    {
        Assert.False(GlobalExceptionHandler.IsDependencyUnavailable(exception), description);
    }
}
