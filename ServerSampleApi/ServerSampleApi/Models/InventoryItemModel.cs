namespace ServerSampleApi.Models
{
    /// <summary>
    /// Một dòng trong túi đồ: người chơi PlayerId đang có Quantity cái vật phẩm ItemId.
    /// Lưu ở bảng "InventoryItems". Khoá chính gồm HAI cột (PlayerId, ItemId):
    /// mỗi người chơi chỉ có tối đa MỘT dòng cho mỗi loại vật phẩm.
    /// </summary>
    public class InventoryItemModel
    {
        public Guid PlayerId { get; set; }

        /// <summary>Mã vật phẩm, luôn lưu chữ thường (ví dụ "potion").</summary>
        public string ItemId { get; set; } = "";

        public int Quantity { get; set; }
    }
}
