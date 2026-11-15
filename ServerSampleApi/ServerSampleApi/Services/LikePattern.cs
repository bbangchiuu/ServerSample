namespace ServerSampleApi.Services
{
    /// <summary>
    /// Tạo pattern cho LIKE / ILIKE của PostgreSQL từ chữ người chơi gõ vào.
    ///
    /// Trong LIKE, "%" nghĩa là "chuỗi bất kỳ" và "_" nghĩa là "một ký tự bất kỳ". Nếu người chơi gõ "%" mà không
    /// thoát (escape), "%%%" sẽ khớp với MỌI tên. Escape = thêm EscapeCharacter phía trước để ký tự đó được hiểu là chữ.
    ///
    /// Tách khỏi ClanService vì không có gì riêng của clan (tìm người chơi, tìm vật phẩm... cũng cần),
    /// và vì là logic thuần nên unit test được trực tiếp (xem LikePatternTests).
    /// "internal": chỉ dùng trong project này, cộng với project unit test (InternalsVisibleTo trong .csproj).
    /// </summary>
    internal static class LikePattern
    {
        /// <summary>Ký tự escape, truyền vào EF.Functions.ILike(..., EscapeCharacter).</summary>
        public const string EscapeCharacter = "\\";

        /// <summary>
        /// Thêm "\" trước các ký tự đặc biệt của LIKE (\, %, _) để chúng được hiểu đúng là chữ.
        /// Phải thay "\" TRƯỚC: nếu thay "%" trước thành "\%", bước thay "\" sau đó sẽ biến nó thành "\\%"
        /// (một dấu "\" thật, rồi "%" lại thành ký tự đặc biệt).
        /// </summary>
        public static string Escape(string text)
        {
            return text
                .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter)
                .Replace("%", EscapeCharacter + "%")
                .Replace("_", EscapeCharacter + "_");
        }

        /// <summary>Pattern "tên có chứa text ở bất kỳ đâu": %text%, với text đã được escape.</summary>
        public static string Contains(string text) => "%" + Escape(text) + "%";
    }
}
