using System.Collections.Concurrent;
using ServerSampleApi.Models;

namespace ServerSampleApi.Services
{
    /// <summary>
    /// Nơi lưu tài khoản, tạm thời trong RAM: tắt server là mất hết. Giai đoạn 3 sẽ thay bằng database.
    ///
    /// Được đăng ký Singleton trong Program.cs: cả server dùng chung MỘT AccountStore.
    /// Nhiều request chạy song song trên nhiều thread cùng đọc/ghi, nên dùng ConcurrentDictionary.
    /// </summary>
    public class AccountStore
    {
        // Key là username, không phân biệt hoa thường: "Bang" và "bang" là cùng một tài khoản.
        private readonly ConcurrentDictionary<string, Account> _accountsByUsername = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Thêm tài khoản mới. Trả về false nếu username đã có người dùng.
        /// TryAdd là thao tác nguyên tử: 2 người đăng ký cùng tên cùng lúc thì chỉ 1 người thành công.
        /// </summary>
        public bool TryAdd(Account account)
        {
            return _accountsByUsername.TryAdd(account.Username, account);
        }

        /// <summary>Tìm tài khoản theo username (không phân biệt hoa thường). Không có thì trả về null.</summary>
        public Account? FindByUsername(string username)
        {
            _accountsByUsername.TryGetValue(username, out Account? account);
            return account;
        }
    }
}
