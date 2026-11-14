using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Auth;
using ServerSampleApi.Dtos;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// API clan: tìm, tạo, xem, vào, rời clan. Luật của clan nằm trong ClanService.
    ///
    /// Controller chỉ làm phần "HTTP": đọc dữ liệu từ request (URL, body, token), gọi service,
    /// rồi chuyển kết quả thành response (200, 201, 204, hoặc lỗi qua ToActionResult).
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/clans")]
    public class ClansController : ControllerBase
    {
        private readonly ClanService _clans;

        public ClansController(ClanService clans)
        {
            _clans = clans;
        }

        // GET /api/clans?search=drag
        [HttpGet]
        [ProducesResponseType(typeof(ClanListResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ClanListResponse>> SearchClans([FromQuery] string? search)
        {
            return Ok(await _clans.SearchAsync(search));
        }

        // POST /api/clans
        [HttpPost]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ClanResponse>> CreateClan(CreateClanRequest request)
        {
            ServiceResult<ClanResponse> result = await _clans.CreateAsync(User.GetPlayerId(), request.Name);
            if (!result.IsSuccess)
            {
                return this.ToActionResult(result.Error!);
            }

            // 201 Created, header Location chỉ tới GET /api/clans/{id}.
            return CreatedAtAction(nameof(GetClan), new { clanId = result.Value.Id }, result.Value);
        }

        // GET /api/clans/{clanId}
        // ":guid": chỉ khớp khi đoạn đó là một Guid, nên "/api/clans/abc" bị 404 ngay ở bước routing.
        // (Riêng "/api/clans/me" thì không cần nhờ nó: đoạn chữ cố định "me" luôn được ưu tiên hơn tham số {clanId}.)
        [HttpGet("{clanId:guid}")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClanResponse>> GetClan(Guid clanId)
        {
            ServiceResult<ClanResponse> result = await _clans.GetAsync(clanId);
            return result.IsSuccess ? Ok(result.Value) : this.ToActionResult(result.Error!);
        }

        // GET /api/clans/me
        [HttpGet("me")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ClanResponse>> GetMyClan()
        {
            ServiceResult<ClanResponse> result = await _clans.GetMyClanAsync(User.GetPlayerId());
            return result.IsSuccess ? Ok(result.Value) : this.ToActionResult(result.Error!);
        }

        // POST /api/clans/{clanId}/join
        [HttpPost("{clanId:guid}/join")]
        [ProducesResponseType(typeof(ClanResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<ClanResponse>> JoinClan(Guid clanId)
        {
            ServiceResult<ClanResponse> result = await _clans.JoinAsync(User.GetPlayerId(), clanId);
            return result.IsSuccess ? Ok(result.Value) : this.ToActionResult(result.Error!);
        }

        // POST /api/clans/me/leave
        [HttpPost("me/leave")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> LeaveClan()
        {
            ServiceResult result = await _clans.LeaveAsync(User.GetPlayerId());

            // 204 No Content: thành công, không có gì để trả về.
            return result.IsSuccess ? NoContent() : this.ToActionResult(result.Error!);
        }
    }
}
