namespace ServerSampleApi.Dtos
{
    /// <summary>
    /// Túi đồ trả về cho client.
    /// Dùng danh sách (List) thay vì Dictionary vì JsonUtility của Unity không đọc được Dictionary.
    /// </summary>
    public class InventoryResponse
    {
        public List<InventoryItemResponse> Items { get; set; } = new();
    }

    public class InventoryItemResponse
    {
        public string ItemId { get; set; } = "";

        public int Quantity { get; set; }
    }
}
