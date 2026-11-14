using Microsoft.AspNetCore.Mvc;
using ServerSampleApi.Services;

namespace ServerSampleApi.Controllers
{
    /// <summary>
    /// Chuyển lỗi nghiệp vụ của service (ServiceError) thành HTTP response. Chỗ DUY NHẤT quyết định
    /// "loại lỗi nào ứng với status nào", mọi controller dùng chung, nên lỗi luôn có cùng một định dạng.
    /// </summary>
    public static class ServiceErrorExtensions
    {
        public static ActionResult ToActionResult(this ControllerBase controller, ServiceError error)
        {
            if (error.Kind == ServiceErrorKind.Validation)
            {
                // Cùng định dạng 400 mà [ApiController] tự trả khi [Required], [StringLength]... sai:
                // {"title":"One or more validation errors occurred.","errors":{"Name":["..."]}}
                controller.ModelState.AddModelError(error.Field ?? "", error.Title);
                return controller.ValidationProblem(controller.ModelState);
            }

            int statusCode = error.Kind switch
            {
                ServiceErrorKind.NotFound => StatusCodes.Status404NotFound,
                ServiceErrorKind.Conflict => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };

            // ProblemDetails: {"title":"Clan is full","detail":"...","status":409,...}
            return controller.Problem(title: error.Title, detail: error.Detail, statusCode: statusCode);
        }
    }
}
