using Microsoft.EntityFrameworkCore;
using ServerSampleApi.Models;

namespace ServerSampleApi.Data
{
    /// <summary>
    /// "Bản đồ" của database cho EF Core: có những bảng nào, mỗi bảng ứng với class C# nào,
    /// mỗi cột có kiểu và giới hạn gì. Đồng thời là cửa ngõ để đọc/ghi dữ liệu:
    ///     db.Players.Find(id), db.Accounts.Add(account), db.SaveChanges()...
    ///
    /// Từ class này, lệnh "dotnet ef migrations add" sinh ra code SQL tạo bảng (migration).
    /// </summary>
    public class GameDbContext : DbContext
    {
        public GameDbContext(DbContextOptions<GameDbContext> options)
            : base(options)
        {
        }

        // Mỗi DbSet là một BẢNG. Tên property = tên bảng ("Accounts", "Players").
        public DbSet<AccountModel> Accounts => Set<AccountModel>();

        public DbSet<PlayerModel> Players => Set<PlayerModel>();

        /// <summary>Mô tả chi tiết từng bảng: khoá chính, độ dài cột, cột nào bắt buộc...</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AccountModel>(account =>
            {
                // Khoá chính: mỗi dòng có một Id duy nhất. (Property tên "Id" vốn đã được EF tự hiểu
                // là khoá chính; viết ra đây cho rõ ràng.)
                account.HasKey(a => a.Id);

                // Giới hạn độ dài giống luật validation của API, để database cũng tự bảo vệ dữ liệu.
                account.Property(a => a.Username).HasMaxLength(20).IsRequired();
                account.Property(a => a.PasswordHash).IsRequired();
            });

            modelBuilder.Entity<PlayerModel>(player =>
            {
                player.HasKey(p => p.Id);
                player.Property(p => p.DisplayName).HasMaxLength(20).IsRequired();

                // Hai property này không lưu vào database:
                //  - SyncRoot: khoá lock, chỉ có ý nghĩa trong RAM của một server.
                //  - Inventory: Dictionary không lưu thẳng thành một cột được, sẽ có bảng riêng ở bước 4.
                // (Truyền tên property dạng chuỗi: viết p => p.SyncRoot sẽ chuyển Lock thành object
                //  và compiler cảnh báo CS9216, dù ở đây không hề khoá gì.)
                player.Ignore(nameof(PlayerModel.SyncRoot));
                player.Ignore(nameof(PlayerModel.Inventory));
            });
        }
    }
}
