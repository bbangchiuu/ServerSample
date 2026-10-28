using System.Collections.Concurrent;
using ServerSampleApi.Models;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Nơi lưu dữ liệu người chơi, tạm thời trong RAM (giống AccountStore). Giai đoạn 3 thay bằng database.
    /// Đăng ký Singleton trong Program.cs.
    /// </summary>
    public class PlayerStore
    {
        private readonly ConcurrentDictionary<Guid, PlayerModel> _players = new();

        public void Add(PlayerModel player)
        {
            _players[player.Id] = player;
        }

        public void Remove(Guid playerId)
        {
            _players.TryRemove(playerId, out _);
        }

        /// <summary>Tìm người chơi theo Id. Không có thì trả về null.</summary>
        public PlayerModel? Find(Guid playerId)
        {
            _players.TryGetValue(playerId, out PlayerModel? player);
            return player;
        }
    }
}
