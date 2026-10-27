namespace ServerSample.Api.Tests;

internal static class TestData
{
    /// <summary>Username ngẫu nhiên, hợp lệ (3-20 ký tự, chữ/số/_), để các test không đụng nhau.</summary>
    public static string NewUsername() => "u_" + Guid.NewGuid().ToString("N")[..12];
}
