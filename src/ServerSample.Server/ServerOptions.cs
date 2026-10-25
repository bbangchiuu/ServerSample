using System.ComponentModel.DataAnnotations;

namespace ServerSample.Server;

/// <summary>
/// Cấu hình server, đọc từ section "Server" trong appsettings.json (Options pattern).
/// Có thể ghi đè bằng biến môi trường, ví dụ: Server__Port=9000
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "Server";

    [Range(1, 65535)]
    public int Port { get; set; } = 7777;

    [Range(1, 100_000)]
    public int MaxConnections { get; set; } = 1000;

    /// <summary>Kích thước payload tối đa của một gói (byte).</summary>
    [Range(1, 16 * 1024 * 1024)]
    public int MaxPayloadSize { get; set; } = 64 * 1024;

    /// <summary>Số gói tối đa chờ gửi cho một client. Vượt quá = client quá chậm, ngắt kết nối.</summary>
    [Range(1, 100_000)]
    public int SendQueueCapacity { get; set; } = 256;

    /// <summary>Không nhận được gói nào trong khoảng này thì coi như client đã chết.</summary>
    [Range(1, 3600)]
    public int HeartbeatTimeoutSeconds { get; set; } = 15;

    [Range(1, 3600)]
    public int HeartbeatCheckIntervalSeconds { get; set; } = 5;

    [Range(1, 10_000)]
    public int MaxChatLength { get; set; } = 500;
}
