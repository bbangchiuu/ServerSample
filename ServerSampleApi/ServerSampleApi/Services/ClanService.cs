using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Hubs;
using ServerSampleApi.Models;

namespace ServerSampleApi.Services
{
    /// <summary>Các lỗi nghiệp vụ của clan. Dùng chung một chỗ để thông báo lỗi luôn giống nhau.</summary>
    public static class ClanErrors
    {
        public static readonly ServiceError NameTooShort = new(ServiceErrorKind.Validation,
            "Clan name must have at least 3 non-space characters.", Field: nameof(CreateClanRequest.Name));

        public static ServiceError NameTaken(string name) => new(ServiceErrorKind.Conflict,
            "Clan name already taken", $"The clan name '{name}' is already in use.");

        public static readonly ServiceError AlreadyInClan = new(ServiceErrorKind.Conflict,
            "Already in a clan", "Leave your current clan first.");

        public static readonly ServiceError ClanFull = new(ServiceErrorKind.Conflict,
            "Clan is full", $"A clan can have at most {ClanService.MaxMembers} members.");

        public static readonly ServiceError NotInClan = new(ServiceErrorKind.NotFound, "Not in a clan");

        public static readonly ServiceError ClanNotFound = new(ServiceErrorKind.NotFound, "Clan not found");
    }

    /// <summary>
    /// Toàn bộ LUẬT của clan: tạo, tìm, vào, rời, chuyển chủ, ai được nhận chat clan.
    ///
    /// Luật:
    ///  - Mỗi người chơi ở TỐI ĐA một clan (khoá chính PlayerId của bảng ClanMembers).
    ///  - Tên clan không trùng, không phân biệt hoa thường.
    ///  - Clan mở: ai cũng vào thẳng được nếu chưa đủ MaxMembers người.
    ///  - Người tạo là chủ clan. Chủ clan rời đi thì người vào sớm nhất làm chủ; người cuối cùng rời đi thì clan bị xoá.
    ///
    /// Service KHÔNG biết gì về HTTP (không có 404, 409, ModelState, User...): nhận playerId và dữ liệu đã tách sẵn,
    /// trả về ServiceResult. Nhờ vậy ai cũng gọi được: ClansController (HTTP), GameHub (SignalR), BackgroundService...
    /// Scoped: dùng GameDbContext (cũng Scoped), mỗi request / mỗi lần gọi hub có một ClanService riêng.
    /// </summary>
    public class ClanService
    {
        public const int MaxMembers = 30;

        /// <summary>Tìm kiếm trả về nhiều nhất bấy nhiêu clan.</summary>
        public const int MaxSearchResults = 20;

        private const string LikeEscapeCharacter = "\\";

        private readonly GameDbContext _db;
        private readonly IHubContext<GameHub, IGameClient> _hub;
        private readonly ILogger<ClanService> _logger;

        public ClanService(GameDbContext db, IHubContext<GameHub, IGameClient> hub, ILogger<ClanService> logger)
        {
            _db = db;
            _hub = hub;
            _logger = logger;
        }

        /// <summary>Tìm clan có tên chứa search (không phân biệt hoa thường). search rỗng: các clan đầu tiên theo tên.</summary>
        public async Task<ClanListResponse> SearchAsync(string? search)
        {
            IQueryable<ClanModel> query = _db.Clans.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                // ILIKE: so khớp không phân biệt hoa thường. "%drag%" = tên có chứa "drag" ở bất kỳ đâu.
                // % và _ là ký tự đặc biệt của LIKE, nên phải "thoát" (escape) chúng nếu người chơi gõ vào,
                // nếu không gõ "%" sẽ khớp với MỌI tên.
                string pattern = "%" + EscapeLikePattern(search.Trim()) + "%";
                query = query.Where(c => EF.Functions.ILike(c.Name, pattern, LikeEscapeCharacter));
            }

            List<ClanSummaryResponse> clans = await query
                .OrderBy(c => c.Name)
                .Take(MaxSearchResults)
                .Select(c => new ClanSummaryResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    MemberCount = c.MemberCount,
                    MaxMembers = MaxMembers,
                })
                .ToListAsync();

            return new ClanListResponse { Clans = clans };
        }

        /// <summary>Tạo clan, người tạo thành chủ clan và là thành viên đầu tiên.</summary>
        public async Task<ServiceResult<ClanResponse>> CreateAsync(Guid playerId, string requestedName)
        {
            // Giống đổi tên hiển thị: kiểm tra độ dài lại SAU khi bỏ khoảng trắng thừa.
            string name = requestedName.Trim();
            if (name.Length < 3)
            {
                return ClanErrors.NameTooShort;
            }

            DateTime now = DateTime.UtcNow;
            var clan = new ClanModel
            {
                Id = Guid.NewGuid(),
                Name = name,
                LeaderId = playerId,
                MemberCount = 1,
                CreatedAt = now,
            };
            _db.Clans.Add(clan);
            _db.ClanMembers.Add(new ClanMemberModel { PlayerId = playerId, ClanId = clan.Id, JoinedAt = now });

            try
            {
                // Một transaction: clan và dòng thành viên đầu tiên cùng được lưu, hoặc không dòng nào được lưu.
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique)
            {
                // Có HAI thứ có thể "trùng", phân biệt bằng tên ràng buộc bị vi phạm:
                //  - unique index IX_Clans_Name: tên clan đã có người dùng;
                //  - khoá chính của ClanMembers (PlayerId): người này đã ở trong một clan.
                // Không kiểm tra trước bằng SELECT: giữa lúc SELECT và INSERT, request khác vẫn có thể chen vào.
                // Database mới là "trọng tài" chắc chắn.
                return unique.ConstraintName == GameDbContext.ClanNameIndex
                    ? ClanErrors.NameTaken(name)
                    : ClanErrors.AlreadyInClan;
            }

            ClanResponse response = (await LoadClanAsync(clan.Id))!;

            // Các thiết bị khác của người tạo (đang mở kết nối hub) cũng biết mình vừa có clan.
            await NotifyMemberJoinedAsync(response, playerId);
            return response;
        }

        /// <summary>Xem một clan kèm danh sách thành viên.</summary>
        public async Task<ServiceResult<ClanResponse>> GetAsync(Guid clanId)
        {
            ClanResponse? clan = await LoadClanAsync(clanId);
            return clan == null ? ClanErrors.ClanNotFound : clan;
        }

        /// <summary>Clan mà người chơi đang ở.</summary>
        public async Task<ServiceResult<ClanResponse>> GetMyClanAsync(Guid playerId)
        {
            Guid? clanId = await FindClanIdOfAsync(playerId);
            if (clanId == null)
            {
                return ClanErrors.NotInClan;
            }

            ClanResponse? clan = await LoadClanAsync(clanId.Value);
            return clan == null ? ClanErrors.NotInClan : clan;
        }

        /// <summary>Vào một clan (clan mở, chỉ cần còn chỗ).</summary>
        public async Task<ServiceResult<ClanResponse>> JoinAsync(Guid playerId, Guid clanId)
        {
            // Hai việc (tăng số thành viên + thêm dòng thành viên) phải cùng thành công hoặc cùng bị huỷ.
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Giữ chỗ trong clan bằng MỘT câu lệnh, kiểm tra "chưa đầy" ngay trong câu UPDATE:
            //   UPDATE "Clans" SET "MemberCount" = "MemberCount" + 1
            //   WHERE "Id" = @clanId AND "MemberCount" < 30
            // PostgreSQL khoá dòng clan này tới khi transaction kết thúc: hai người cùng tranh chỗ cuối cùng thì
            // người sau phải chờ, rồi thấy MemberCount đã là 30 nên không cập nhật được dòng nào.
            int updatedRows = await _db.Clans
                .Where(c => c.Id == clanId && c.MemberCount < MaxMembers)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.MemberCount, c => c.MemberCount + 1));

            if (updatedRows == 0)
            {
                // Không giữ được chỗ: hoặc clan không tồn tại, hoặc đã đầy.
                return await _db.Clans.AnyAsync(c => c.Id == clanId)
                    ? ClanErrors.ClanFull
                    : ClanErrors.ClanNotFound;
            }

            _db.ClanMembers.Add(new ClanMemberModel { PlayerId = playerId, ClanId = clanId, JoinedAt = DateTime.UtcNow });
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Khoá chính PlayerId bị trùng: người này đã ở trong một clan (có thể chính clan này).
                // Return luôn, KHÔNG Commit: "await using" huỷ transaction, nên chỗ vừa giữ (+1) cũng được trả lại.
                return ClanErrors.AlreadyInClan;
            }

            await transaction.CommitAsync();

            ClanResponse? clan = await LoadClanAsync(clanId);
            if (clan == null)
            {
                return ClanErrors.ClanNotFound; // vừa vào xong thì clan đã bị xoá: chỉ xảy ra khi chính người này rời clan ngay sau đó
            }

            // Đã COMMIT rồi mới báo, giống nhận thưởng hằng ngày: báo trước mà commit thất bại thì client
            // sẽ thấy một thành viên không có thật.
            await NotifyMemberJoinedAsync(clan, playerId);
            return clan;
        }

        /// <summary>Rời clan đang ở.</summary>
        public async Task<ServiceResult> LeaveAsync(Guid playerId)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();

            Guid? clanId = await FindClanIdOfAsync(playerId);
            if (clanId == null)
            {
                return ClanErrors.NotInClan;
            }

            // Việc ĐẦU TIÊN: cập nhật dòng clan, để khoá nó tới hết transaction. Mọi lần vào/rời KHÁC của cùng clan
            // cũng bắt đầu bằng UPDATE dòng này, nên phải xếp hàng chờ nhau. Nhờ vậy các bước bên dưới (chọn chủ mới,
            // xoá clan khi hết người) không bị người khác chen ngang giữa chừng.
            await _db.Clans
                .Where(c => c.Id == clanId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.MemberCount, c => c.MemberCount - 1));

            int deletedRows = await _db.ClanMembers
                .Where(m => m.PlayerId == playerId && m.ClanId == clanId)
                .ExecuteDeleteAsync();
            if (deletedRows == 0)
            {
                // Trong lúc chờ khoá, một request "rời clan" khác của chính người này đã xong trước.
                // Không Commit: số thành viên vừa trừ được trả lại.
                return ClanErrors.NotInClan;
            }

            // Đọc lại SAU khi đã giữ khoá: thấy đúng số thành viên và chủ clan mới nhất.
            ClanModel clan = await _db.Clans.AsNoTracking().FirstAsync(c => c.Id == clanId);
            Guid? leaderId = clan.LeaderId;
            if (clan.MemberCount == 0)
            {
                // Người cuối cùng rời đi: xoá clan, tên clan được dùng lại.
                await _db.Clans.Where(c => c.Id == clanId).ExecuteDeleteAsync();
                leaderId = null;
            }
            else if (clan.LeaderId == playerId)
            {
                // Chủ clan rời đi: người vào sớm nhất làm chủ (trùng thời điểm thì xếp theo PlayerId cho cố định).
                Guid newLeaderId = await _db.ClanMembers
                    .Where(m => m.ClanId == clanId)
                    .OrderBy(m => m.JoinedAt)
                    .ThenBy(m => m.PlayerId)
                    .Select(m => m.PlayerId)
                    .FirstAsync();

                await _db.Clans
                    .Where(c => c.Id == clanId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.LeaderId, newLeaderId));
                leaderId = newLeaderId;
            }

            // Gom dữ liệu để báo tin, ngay trong lúc còn giữ khoá: danh sách người ở lại là chính xác.
            List<Guid> remainingIds = await _db.ClanMembers
                .Where(m => m.ClanId == clanId)
                .Select(m => m.PlayerId)
                .ToListAsync();
            string displayName = await _db.Players
                .Where(p => p.Id == playerId)
                .Select(p => p.DisplayName)
                .FirstAsync();

            await transaction.CommitAsync();

            // Người nhận: những người ở lại + chính người vừa rời (để các thiết bị khác của họ cũng biết).
            remainingIds.Add(playerId);
            // Đã commit: Redis sập thì chỉ ghi log, vẫn trả thành công (xem RealtimeNotification).
            await RealtimeNotification.TrySendAsync(_logger, "ClanMemberLeft",
                () => _hub.Clients.Users(remainingIds.Select(id => id.ToString()).ToList())
                    .ClanMemberLeft(new ClanMemberEvent
                    {
                        ClanId = clan.Id,
                        PlayerId = playerId,
                        DisplayName = displayName,
                        LeaderId = leaderId,
                        MemberCount = clan.MemberCount,
                    }));

            return ServiceResult.Success();
        }

        /// <summary>
        /// playerId (dạng chuỗi, cho Clients.Users) của mọi thành viên cùng clan với người này, kể cả chính họ.
        /// Danh sách rỗng nếu người này không ở clan nào. Dùng cho chat clan (GameHub).
        /// </summary>
        public Task<List<string>> GetClanMateIdsAsync(Guid playerId)
        {
            // Một câu SQL:
            //   SELECT m."PlayerId" FROM "ClanMembers" m
            //   WHERE EXISTS (SELECT 1 FROM "ClanMembers" me WHERE me."PlayerId" = @playerId AND me."ClanId" = m."ClanId")
            return _db.ClanMembers
                .Where(m => _db.ClanMembers.Any(me => me.PlayerId == playerId && me.ClanId == m.ClanId))
                .Select(m => m.PlayerId.ToString())
                .ToListAsync();
        }

        /// <summary>
        /// Báo "playerId vừa vào clan" cho mọi thành viên hiện tại của clan (gồm cả chính người đó).
        /// Clients.Users(...): mọi kết nối của những người trong danh sách, trên server nào cũng được (backplane).
        /// Gọi sau khi đã commit: Redis sập thì chỉ ghi log, vẫn trả thành công (xem RealtimeNotification).
        /// </summary>
        private Task NotifyMemberJoinedAsync(ClanResponse clan, Guid playerId)
        {
            ClanMemberResponse? member = clan.Members.FirstOrDefault(m => m.PlayerId == playerId);
            if (member == null)
            {
                return Task.CompletedTask; // đã rời clan ngay sau khi vào: không còn gì để báo
            }

            return RealtimeNotification.TrySendAsync(_logger, "ClanMemberJoined",
                () => _hub.Clients.Users(clan.Members.Select(m => m.PlayerId.ToString()).ToList())
                    .ClanMemberJoined(new ClanMemberEvent
                    {
                        ClanId = clan.Id,
                        PlayerId = playerId,
                        DisplayName = member.DisplayName,
                        LeaderId = clan.LeaderId,
                        MemberCount = clan.MemberCount,
                    }));
        }

        /// <summary>Clan mà người chơi đang ở, null nếu không ở clan nào.</summary>
        private Task<Guid?> FindClanIdOfAsync(Guid playerId)
        {
            return _db.ClanMembers
                .Where(m => m.PlayerId == playerId)
                .Select(m => (Guid?)m.ClanId)
                .FirstOrDefaultAsync();
        }

        /// <summary>Đọc clan kèm danh sách thành viên. null nếu clan không tồn tại.</summary>
        private async Task<ClanResponse?> LoadClanAsync(Guid clanId)
        {
            ClanModel? clan = await _db.Clans.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clanId);
            if (clan == null)
            {
                return null;
            }

            // m.Player.DisplayName: EF tự JOIN sang bảng Players, vẫn là MỘT câu SQL cho cả danh sách.
            Guid leaderId = clan.LeaderId;
            List<ClanMemberResponse> members = await _db.ClanMembers
                .Where(m => m.ClanId == clanId)
                .OrderBy(m => m.JoinedAt)
                .ThenBy(m => m.PlayerId)
                .Select(m => new ClanMemberResponse
                {
                    PlayerId = m.PlayerId,
                    DisplayName = m.Player.DisplayName,
                    IsLeader = m.PlayerId == leaderId,
                    JoinedAt = m.JoinedAt,
                })
                .ToListAsync();

            return new ClanResponse
            {
                Id = clan.Id,
                Name = clan.Name,
                LeaderId = clan.LeaderId,
                MemberCount = clan.MemberCount,
                MaxMembers = MaxMembers,
                CreatedAt = clan.CreatedAt,
                Members = members,
            };
        }

        /// <summary>Thêm "\" trước các ký tự đặc biệt của LIKE (\, %, _) để chúng được hiểu đúng là chữ.</summary>
        private static string EscapeLikePattern(string text)
        {
            return text
                .Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter)
                .Replace("%", LikeEscapeCharacter + "%")
                .Replace("_", LikeEscapeCharacter + "_");
        }
    }
}
