using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServerSampleApi.Auth;
using ServerSampleApi.Data;
using ServerSampleApi.Dtos;
using ServerSampleApi.Models;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API clan: tìm, tạo, xem, vào, rời clan.
    ///
    /// Luật:
    ///  - Mỗi người chơi ở TỐI ĐA một clan (khoá chính PlayerId của bảng ClanMembers).
    ///  - Tên clan không trùng, không phân biệt hoa thường.
    ///  - Clan mở: ai cũng vào thẳng được nếu chưa đủ MaxMembers người.
    ///  - Người tạo là chủ clan. Chủ clan rời đi thì người vào sớm nhất làm chủ; người cuối cùng rời đi thì clan bị xoá.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/clans")]
    public class ClansController : ControllerBase
    {
        public const int MaxMembers = 30;

        /// <summary>Tìm kiếm trả về nhiều nhất bấy nhiêu clan.</summary>
        public const int MaxSearchResults = 20;

        private readonly GameDbContext _db;

        public ClansController(GameDbContext db)
        {
            _db = db;
        }

        // GET /api/clans?search=drag
        [HttpGet]
        [ProducesResponseType(typeof(ClanListResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ClanListResponse>> SearchClans([FromQuery] string? search)
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

            return Ok(new ClanListResponse { Clans = clans });
        }

        // POST /api/clans
        [HttpPost]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ClanResponse>> CreateClan(CreateClanRequest request)
        {
            Guid playerId = User.GetPlayerId();

            // Giống đổi tên hiển thị: kiểm tra độ dài lại SAU khi bỏ khoảng trắng thừa.
            string name = request.Name.Trim();
            if (name.Length < 3)
            {
                ModelState.AddModelError(nameof(request.Name), "Clan name must have at least 3 non-space characters.");
                return ValidationProblem(ModelState);
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
                    ? Problem(
                        title: "Clan name already taken",
                        detail: $"The clan name '{name}' is already in use.",
                        statusCode: StatusCodes.Status409Conflict)
                    : AlreadyInClan();
            }

            ClanResponse response = (await LoadClanAsync(clan.Id))!;
            return CreatedAtAction(nameof(GetClan), new { clanId = clan.Id }, response);
        }

        // GET /api/clans/{clanId}
        // ":guid": chỉ khớp khi đoạn đó là một Guid, nên "/api/clans/me" không bị hiểu nhầm là clanId = "me".
        [HttpGet("{clanId:guid}")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClanResponse>> GetClan(Guid clanId)
        {
            ClanResponse? clan = await LoadClanAsync(clanId);
            return clan == null ? ClanNotFound() : Ok(clan);
        }

        // GET /api/clans/me
        [HttpGet("me")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClanResponse>> GetMyClan()
        {
            Guid? clanId = await FindClanIdOfAsync(User.GetPlayerId());
            if (clanId == null)
            {
                return NotInClan();
            }

            ClanResponse? clan = await LoadClanAsync(clanId.Value);
            return clan == null ? NotInClan() : Ok(clan);
        }

        // POST /api/clans/{clanId}/join
        [HttpPost("{clanId:guid}/join")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ClanResponse>> JoinClan(Guid clanId)
        {
            Guid playerId = User.GetPlayerId();

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
                    ? Problem(
                        title: "Clan is full",
                        detail: $"A clan can have at most {MaxMembers} members.",
                        statusCode: StatusCodes.Status409Conflict)
                    : ClanNotFound();
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
                return AlreadyInClan();
            }

            await transaction.CommitAsync();
            return Ok(await LoadClanAsync(clanId));
        }

        // POST /api/clans/me/leave
        [HttpPost("me/leave")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> LeaveClan()
        {
            Guid playerId = User.GetPlayerId();

            await using var transaction = await _db.Database.BeginTransactionAsync();

            Guid? clanId = await FindClanIdOfAsync(playerId);
            if (clanId == null)
            {
                return NotInClan();
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
                return NotInClan();
            }

            // Đọc lại SAU khi đã giữ khoá: thấy đúng số thành viên và chủ clan mới nhất.
            ClanModel clan = await _db.Clans.AsNoTracking().FirstAsync(c => c.Id == clanId);
            if (clan.MemberCount == 0)
            {
                // Người cuối cùng rời đi: xoá clan, tên clan được dùng lại.
                await _db.Clans.Where(c => c.Id == clanId).ExecuteDeleteAsync();
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
            }

            await transaction.CommitAsync();

            // 204 No Content: thành công, không có gì để trả về.
            return NoContent();
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

        private ObjectResult AlreadyInClan() => Problem(
            title: "Already in a clan",
            detail: "Leave your current clan first.",
            statusCode: StatusCodes.Status409Conflict);

        private ObjectResult NotInClan() => Problem(
            title: "Not in a clan",
            statusCode: StatusCodes.Status404NotFound);

        private ObjectResult ClanNotFound() => Problem(
            title: "Clan not found",
            statusCode: StatusCodes.Status404NotFound);

        private const string LikeEscapeCharacter = "\\";

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
