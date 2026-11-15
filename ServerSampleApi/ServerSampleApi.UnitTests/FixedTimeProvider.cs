namespace ServerSampleApi.UnitTests;

/// <summary>
/// Đồng hồ GIẢ cho unit test: luôn chỉ đúng giờ mình đặt, chỉ nhích khi mình gọi Advance.
///
/// TimeProvider là class abstract của .NET; TimeProvider.System (giờ thật) và class này đều là "một loại đồng hồ".
/// Code nhận TimeProvider qua constructor nên không biết (và không cần biết) mình đang dùng loại nào.
/// Chỉ cần ghi đè GetUtcNow(): đó là hàm TokenService dùng.
///
/// (Microsoft có sẵn bản đầy đủ hơn: FakeTimeProvider trong package Microsoft.Extensions.TimeProvider.Testing,
/// hỗ trợ cả timer và Task.Delay. Ở đây tự viết cho thấy rõ bên trong nó đơn giản thế nào.)
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Cho thời gian trôi đi một khoảng, ví dụ Advance(TimeSpan.FromMinutes(16)).</summary>
    public void Advance(TimeSpan duration) => _now += duration;
}
