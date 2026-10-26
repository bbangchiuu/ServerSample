using System.Collections.Concurrent;
using ServerSample.Shared;

namespace ServerSample.Server.Networking;

/// <summary>
/// Danh sách các session đang online. Được đọc/ghi từ nhiều thread cùng lúc
/// (accept loop thêm vào, session tự xóa khi đóng, heartbeat duyệt qua, chat broadcast duyệt qua),
/// nên dùng ConcurrentDictionary thay vì Dictionary + lock.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<long, ClientSession> _sessions = new();
    private long _lastId;

    public int Count => _sessions.Count;

    /// <summary>Duyệt an toàn ngay cả khi có session được thêm/xóa trong lúc duyệt.</summary>
    public IEnumerable<ClientSession> All => _sessions.Values;

    public long NextId() => Interlocked.Increment(ref _lastId);

    public void Add(ClientSession session) => _sessions[session.Id] = session;

    public void Remove(ClientSession session) => _sessions.TryRemove(session.Id, out _);

    public void Broadcast(Packet packet)
    {
        foreach (ClientSession session in _sessions.Values)
            session.Send(packet);
    }
}
