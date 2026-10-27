using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using ServerSample.Api.Common;
using ServerSample.Api.Data;

namespace ServerSample.Api.Accounts;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // Mọi endpoint trong nhóm này đều bắt đầu bằng /api/accounts
        RouteGroupBuilder group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapPost("/register", Register).WithValidation<RegisterRequest>();
        group.MapPost("/login", Login).WithValidation<LoginRequest>();
    }

    // POST /api/accounts/register
    // Tham số được ASP.NET Core tự điền: request lấy từ JSON body, các service lấy từ DI.
    private static Results<Created<AccountResponse>, ProblemHttpResult> Register(
        RegisterRequest request,
        InMemoryDatabase db,
        IPasswordHasher<Account> passwordHasher,
        TimeProvider time)
    {
        var account = new Account { Id = Guid.NewGuid(), Username = request.Username, CreatedAt = time.GetUtcNow() };
        account.PasswordHash = passwordHasher.HashPassword(account, request.Password);

        // Tạo Player trước, để ngay khi tài khoản xuất hiện thì Player của nó cũng đã có.
        db.Players[account.Id] = new Player { Id = account.Id, DisplayName = account.Username };

        // TryAdd là thao tác nguyên tử: 2 người đăng ký cùng tên cùng lúc thì chỉ 1 người thành công.
        if (!db.AccountsByUsername.TryAdd(account.Username, account))
        {
            db.Players.TryRemove(account.Id, out _);
            return TypedResults.Problem(
                title: "Username already taken",
                detail: $"The username '{request.Username}' is already in use.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // 201 Created + header Location chỉ tới tài nguyên vừa tạo.
        return TypedResults.Created($"/api/players/{account.Id}", new AccountResponse(account.Id, account.Username));
    }

    // POST /api/accounts/login
    // Giai đoạn này chỉ trả về PlayerId. Giai đoạn 4 sẽ trả về JWT token để bảo vệ các API khác.
    private static Results<Ok<AccountResponse>, ProblemHttpResult> Login(
        LoginRequest request,
        InMemoryDatabase db,
        IPasswordHasher<Account> passwordHasher)
    {
        // Sai tên hay sai mật khẩu đều trả CÙNG một lỗi, để kẻ xấu không dò được tên tài khoản nào tồn tại.
        if (!db.AccountsByUsername.TryGetValue(request.Username, out Account? account)
            || passwordHasher.VerifyHashedPassword(account, account.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        {
            return TypedResults.Problem(title: "Invalid username or password", statusCode: StatusCodes.Status401Unauthorized);
        }

        return TypedResults.Ok(new AccountResponse(account.Id, account.Username));
    }
}
