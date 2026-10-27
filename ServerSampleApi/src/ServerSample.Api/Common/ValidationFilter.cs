using System.ComponentModel.DataAnnotations;

namespace ServerSample.Api.Common;

/// <summary>
/// Endpoint filter: chạy TRƯỚC handler, kiểm tra các attribute [Required], [StringLength]...
/// trên object request. Sai thì trả 400 kèm danh sách lỗi, handler không bao giờ được gọi.
///
/// Từ .NET 10, Minimal API đã có validation dựng sẵn (builder.Services.AddValidation()).
/// Filter tự viết này được giữ lại để học cách endpoint filter hoạt động (xem bài tập 8 trong docs/phase-2.md).
/// </summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        T? request = context.Arguments.OfType<T>().FirstOrDefault();
        if (request is null)
            return TypedResults.Problem(title: "Request body is required", statusCode: StatusCodes.Status400BadRequest);

        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            Dictionary<string, string[]> errors = results
                .GroupBy(r => r.MemberNames.FirstOrDefault() ?? "")
                .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalid value.").ToArray());
            return TypedResults.ValidationProblem(errors);
        }

        return await next(context);
    }
}

public static class ValidationFilterExtensions
{
    /// <summary>Gắn ValidationFilter cho endpoint: <c>group.MapPost(...).WithValidation&lt;RegisterRequest&gt;()</c>.</summary>
    public static RouteHandlerBuilder WithValidation<T>(this RouteHandlerBuilder builder) where T : class =>
        builder
            .AddEndpointFilter<ValidationFilter<T>>()
            .ProducesValidationProblem();
}
