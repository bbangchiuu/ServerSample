namespace ServerSampleApi.Services
{
    /// <summary>Loại lỗi nghiệp vụ. Controller dựa vào đây để chọn HTTP status (400, 404, 409).</summary>
    public enum ServiceErrorKind
    {
        /// <summary>Dữ liệu gửi lên sai (400). Có Field: lỗi thuộc trường nào.</summary>
        Validation,

        /// <summary>Không tìm thấy thứ được yêu cầu (404).</summary>
        NotFound,

        /// <summary>Yêu cầu hợp lệ nhưng xung đột với dữ liệu đang có (409): tên trùng, clan đầy...</summary>
        Conflict,
    }

    /// <summary>
    /// Một lỗi nghiệp vụ DỰ ĐOÁN ĐƯỢC (clan đầy, tên trùng...), do service trả về.
    /// Không phải exception: đây là kết quả bình thường của một yêu cầu, không phải sự cố.
    /// Service không biết gì về HTTP; controller mới quyết định gửi lỗi này về client ra sao.
    /// </summary>
    public sealed record ServiceError(ServiceErrorKind Kind, string Title, string? Detail = null, string? Field = null);

    /// <summary>
    /// Kết quả của một thao tác KHÔNG trả dữ liệu: thành công, hoặc thất bại kèm ServiceError.
    /// Nhờ implicit operator, trong service chỉ cần viết "return ClanErrors.NotInClan;".
    /// </summary>
    public class ServiceResult
    {
        protected ServiceResult(ServiceError? error)
        {
            Error = error;
        }

        /// <summary>Lỗi nếu thất bại, null nếu thành công.</summary>
        public ServiceError? Error { get; }

        public bool IsSuccess => Error == null;

        public static ServiceResult Success() => new(null);

        public static implicit operator ServiceResult(ServiceError error) => new(error);
    }

    /// <summary>
    /// Kết quả của một thao tác CÓ dữ liệu trả về: thành công kèm Value, hoặc thất bại kèm Error.
    /// Trong service viết "return clan;" (thành công) hoặc "return ClanErrors.ClanFull;" (thất bại):
    /// implicit operator tự bọc giá trị thành ServiceResult.
    /// </summary>
    public sealed class ServiceResult<T> : ServiceResult
    {
        private readonly T? _value;

        private ServiceResult(T? value, ServiceError? error)
            : base(error)
        {
            _value = value;
        }

        /// <summary>Giá trị khi thành công. Đọc khi thất bại là lỗi lập trình: phải kiểm tra IsSuccess trước.</summary>
        public T Value => IsSuccess
            ? _value!
            : throw new InvalidOperationException($"Cannot read Value of a failed result: {Error!.Title}");

        public static implicit operator ServiceResult<T>(T value) => new(value, null);

        public static implicit operator ServiceResult<T>(ServiceError error) => new(default, error);
    }
}
