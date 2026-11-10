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

        // Mỗi DbSet là một BẢNG. Tên property = tên bảng ("Accounts", "Players", "InventoryItems").
        public DbSet<AccountModel> Accounts => Set<AccountModel>();

        public DbSet<PlayerModel> Players => Set<PlayerModel>();

        public DbSet<InventoryItemModel> InventoryItems => Set<InventoryItemModel>();
        public DbSet<LogEventModel> LogEvents => Set<LogEventModel>();

        public DbSet<RefreshTokenModel> RefreshTokens => Set<RefreshTokenModel>();

        public DbSet<ClanModel> Clans => Set<ClanModel>();

        public DbSet<ClanMemberModel> ClanMembers => Set<ClanMemberModel>();

        /// <summary>Tên unique index trên Clans.Name: dùng để biết lỗi "trùng" là do trùng tên clan (xem ClansController).</summary>
        public const string ClanNameIndex = "IX_Clans_Name";

        /// <summary>Mô tả chi tiết từng bảng: khoá chính, khoá ngoại, độ dài cột, ràng buộc...</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // citext: kiểu chữ "không phân biệt hoa thường" của PostgreSQL (cần bật extension này).
            modelBuilder.HasPostgresExtension("citext");

            modelBuilder.Entity<AccountModel>(account =>
            {
                // Khoá chính: mỗi dòng có một Id duy nhất. (Property tên "Id" vốn đã được EF tự hiểu
                // là khoá chính; viết ra đây cho rõ ràng.)
                account.HasKey(a => a.Id);

                // Cột kiểu citext: "Bang" và "bang" được coi là BẰNG NHAU, khi tìm kiếm lẫn khi so trùng.
                account.Property(a => a.Username).HasColumnType("citext").IsRequired();

                // Unique index: database TỪ CHỐI lưu 2 dòng cùng Username. Thay cho ConcurrentDictionary.TryAdd
                // trong RAM, và vẫn đúng kể cả khi chạy nhiều server cùng lúc dùng chung một database.
                account.HasIndex(a => a.Username).IsUnique();

                account.Property(a => a.PasswordHash).IsRequired();
            });

            modelBuilder.Entity<PlayerModel>(player =>
            {
                player.HasKey(p => p.Id);
                player.Property(p => p.DisplayName).HasMaxLength(20).IsRequired();

                // Khoá ngoại 1-1: Players.Id phải là Id của một dòng có thật trong Accounts.
                // Database không cho tồn tại nhân vật "mồ côi" không có tài khoản.
                // Cascade: xoá tài khoản thì nhân vật của nó cũng bị xoá theo.
                player.HasOne<AccountModel>()
                    .WithOne()
                    .HasForeignKey<PlayerModel>(p => p.Id)
                    .OnDelete(DeleteBehavior.Cascade);

                // Quan hệ 1-nhiều: một người chơi có nhiều dòng trong InventoryItems.
                player.HasMany(p => p.Inventory)
                    .WithOne()
                    .HasForeignKey(i => i.PlayerId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<InventoryItemModel>(item =>
            {
                // Khoá chính gồm 2 cột: mỗi người chơi chỉ có MỘT dòng cho mỗi loại vật phẩm.
                item.HasKey(i => new { i.PlayerId, i.ItemId });
                item.Property(i => i.ItemId).HasMaxLength(50);
            });

            modelBuilder.Entity<RefreshTokenModel>(token =>
            {
                token.HasKey(t => t.Id);

                // SHA-256 viết dạng hex luôn dài đúng 64 ký tự.
                // Unique index: tìm theo bản băm rất nhanh (mỗi lần làm mới token đều tìm), và không bao giờ trùng.
                token.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
                token.HasIndex(t => t.TokenHash).IsUnique();

                // Một tài khoản có nhiều refresh token (đăng nhập trên nhiều máy). Xoá tài khoản thì xoá hết token.
                token.HasOne<AccountModel>()
                    .WithMany()
                    .HasForeignKey(t => t.AccountId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<ClanModel>(clan =>
            {
                clan.HasKey(c => c.Id);

                // Giống Username: "Dragons" và "dragons" là cùng một tên, database từ chối tên thứ hai.
                clan.Property(c => c.Name).HasColumnType("citext").HasMaxLength(20).IsRequired();
                clan.HasIndex(c => c.Name).IsUnique().HasDatabaseName(ClanNameIndex);

                // Chủ clan phải là một người chơi có thật. Restrict: không cho xoá người chơi đang làm chủ clan
                // (phải chuyển chủ hoặc rời clan trước), tránh clan "không có chủ".
                clan.HasOne<PlayerModel>()
                    .WithMany()
                    .HasForeignKey(c => c.LeaderId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<ClanMemberModel>(member =>
            {
                // Khoá chính CHỈ là PlayerId (không phải PlayerId + ClanId): một người chơi chỉ có một dòng,
                // nên không thể ở hai clan cùng lúc.
                member.HasKey(m => m.PlayerId);

                member.HasOne(m => m.Player)
                    .WithOne()
                    .HasForeignKey<ClanMemberModel>(m => m.PlayerId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Một clan có nhiều thành viên. Xoá clan thì xoá luôn các dòng thành viên của nó.
                // EF tự tạo index trên ClanId, nên "lấy mọi thành viên của clan X" không phải quét cả bảng.
                member.HasOne<ClanModel>()
                    .WithMany()
                    .HasForeignKey(m => m.ClanId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
