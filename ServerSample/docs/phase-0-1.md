# Giai đoạn 0–1: Nền tảng C# phía server

## Mục tiêu

Hiểu một socket server game hoạt động thế nào ở mức thấp nhất, và viết nó theo cách của .NET hiện đại.
Sau giai đoạn này bạn phải tự trả lời được mọi câu hỏi trong phần **Tự kiểm tra** ở cuối.

## Đọc code theo thứ tự này

| # | File | Học được gì |
|---|------|-------------|
| 1 | `Shared/PacketSerializer.cs` | TCP là dòng byte, vì sao cần length-prefix, không tin dữ liệu client, `ArrayPool` |
| 2 | `tests/.../PacketSerializerTests.cs` | Gói dính nhau, gói bị chia nhỏ, cách test bằng stream giả |
| 3 | `Server/Program.cs` | Generic Host, DI, Options pattern, cấu hình Serilog |
| 4 | `Server/Networking/TcpServerService.cs` | `BackgroundService`, accept loop, fire-and-forget **an toàn**, tắt server êm |
| 5 | `Server/Networking/ClientSession.cs` | 2 vòng lặp song song, `Channel<T>`, `CancellationToken`, `Interlocked` |
| 6 | `Server/Networking/SessionManager.cs` | `ConcurrentDictionary`, broadcast |
| 7 | `Server/Networking/HeartbeatMonitor.cs` | `PeriodicTimer`, phát hiện client chết |
| 8 | `Client/GameClient.cs` | `SemaphoreSlim` làm lock async, so sánh với Channel |
| 9 | `Client/Program.cs` (BotRunner) | Chạy hàng trăm tác vụ song song mà không cần hàng trăm thread |

## Kiến thức cốt lõi

### async/await khác coroutine Unity thế nào?
- Coroutine chạy trên main thread, tiếp tục ở frame sau. `await` trên server **có thể tiếp tục ở thread khác**
  trong ThreadPool. Hai đoạn code của hai client hoàn toàn có thể chạy **cùng lúc** trên hai CPU core.
- Hệ quả: mọi state dùng chung (danh sách session, bộ đếm...) phải thread-safe.
- Trong lúc `await` chờ mạng, **không có thread nào bị chặn**. Vì vậy 1000 client không cần 1000 thread.

### Vì sao không dùng `lock` quanh `await`?
`lock` không cho phép `await` bên trong (compiler báo lỗi). Thay vào đó có thể dùng:
- `SemaphoreSlim(1,1)` + `WaitAsync()`, như trong `GameClient`
- `Channel<T>`: một nơi duy nhất làm việc, những nơi khác chỉ xếp hàng, như trong `ClientSession`

### CancellationToken
Là cách chuẩn để nói "dừng lại" với code async. Gần như mọi hàm async của .NET đều nhận một token.
Token phải được truyền xuống tận các lời gọi I/O thì mới có tác dụng.

### Server không bao giờ tin client
- Độ dài gói: kiểm tra `MaxPayloadSize` **trước khi** cấp phát bộ nhớ.
- Loại gói lạ: ngắt kết nối.
- Chat quá dài: từ chối.
- Client đọc chậm làm hàng đợi gửi đầy: ngắt, để không làm phình RAM server.

## So sánh với server cũ (ToolServerSocket)

Mở `ToolServerSocket\Server` và tự trả lời:
1. Server cũ tách gói thế nào? Có xử lý gói dính hoặc gói bị chia nhỏ không?
2. Có giới hạn kích thước gói không?
3. Ghi socket từ nhiều thread có được bảo vệ không?
4. Phát hiện client chết bằng cách nào?
5. Dùng `Thread`, callback (`BeginReceive`) hay `async/await`?

Ghi câu trả lời vào cuối file này. Đây là bài tập quan trọng nhất của giai đoạn.

## Bài tập (tự làm, theo độ khó tăng dần)

1. **Lệnh `/who`**: thêm `MessageType.WhoRequest` / `WhoResponse`, server trả về danh sách ID đang online.
2. **Đặt tên**: thêm `SetName`, chat hiển thị tên thay vì `#id`. Lưu tên ở đâu cho thread-safe?
3. **Tin nhắn riêng**: `/w <id> <text>`, chỉ gửi cho một người.
4. **Rate limit**: mỗi session tối đa 5 tin chat mỗi giây, vượt quá thì cảnh báo, lặp lại thì ngắt.
5. **Thống kê**: một `BackgroundService` mới, cứ 30 giây log số người online và số gói nhận/gửi mỗi giây.
6. **Viết test cho `PacketDispatcher`**: cần tách `ClientSession` sau một interface để dễ test.
7. **Load test**: chạy `--bots 1000`, rồi `--bots 5000`. Server bắt đầu chậm ở đâu? Xem CPU/RAM trong Task Manager.
8. *(Nâng cao)* Viết lại `PacketSerializer` dùng `System.IO.Pipelines`, đo lại bằng bot.

## Tự kiểm tra

- Vì sao một lần `ReadAsync` không chắc trả về đúng một gói?
- Nếu bỏ `Channel` và gọi thẳng `stream.WriteAsync` từ `Broadcast`, chuyện gì có thể xảy ra?
- Rút dây mạng của client: server biết bằng cách nào, sau bao lâu?
- `_ = RunSessionAsync(session)`: vì sao ở đây được phép không `await`? Khi nào việc đó nguy hiểm?
- `Interlocked.Increment` khác `_count++` ở điểm nào?
- Khi bấm Ctrl+C, server tắt theo thứ tự nào?

## Môi trường

- Đang dùng .NET 10 (LTS, được hỗ trợ tới 11/2028). Phiên bản .NET khai báo trong `Directory.Build.props`.
- Docker Desktop: cần từ giai đoạn 3 cho `docker-compose.yml` (Postgres + Redis).
