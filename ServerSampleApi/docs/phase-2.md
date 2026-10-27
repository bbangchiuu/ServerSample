# Giai đoạn 2: HTTP API với ASP.NET Core

## Mục tiêu
Hiểu HTTP và REST, và viết được một API game có: kiểm tra dữ liệu đầu vào, lỗi trả về đúng chuẩn,
tài liệu API tự sinh, test tự động. Sau giai đoạn này phải tự trả lời được phần **Tự kiểm tra** ở cuối.

## TCP server và HTTP API khác nhau thế nào?
| | TCP server (giai đoạn 1) | HTTP API (giai đoạn 2) |
|---|---|---|
| Kết nối | Giữ mở suốt phiên chơi | Mỗi request một lần hỏi – đáp, xong là thôi |
| Server chủ động gửi? | Có (chat broadcast, thông báo) | Không, chỉ trả lời khi client hỏi |
| Giao thức | Tự thiết kế (length-prefix + MessageType) | Chuẩn chung: method, URL, status code, JSON |
| Hợp với | Realtime: chat, trận đấu, vị trí | Dữ liệu "hỏi là có": đăng nhập, profile, túi đồ, shop, bảng xếp hạng |

Game thật thường dùng **cả hai**: HTTP cho phần lớn tính năng, socket cho phần realtime.

## Đọc code theo thứ tự này
| # | File | Học được gì |
|---|------|-------------|
| 1 | `src/ServerSample.Api/Program.cs` | Đăng ký service (DI), middleware pipeline, OpenAPI |
| 2 | `Accounts/AccountContracts.cs` | Request/response tách khỏi dữ liệu bên trong, validation attribute |
| 3 | `Accounts/AccountEndpoints.cs` | `MapGroup`, `MapPost`, tham số tự điền (body, DI), `TypedResults`, 201/401/409 |
| 4 | `Common/ValidationFilter.cs` | Endpoint filter: chạy trước handler, chặn request sai |
| 5 | `Players/PlayerEndpoints.cs` | Route parameter, route constraint `:guid`, server là nguồn sự thật, `lock` chống gian lận |
| 6 | `Data/` | Vì sao `Account` tách khỏi `Player`, vì sao phải `lock` |
| 7 | `tests/ServerSample.Api.Tests/` | Integration test với `WebApplicationFactory` |

## Kiến thức cốt lõi

### HTTP request và response
```
POST /api/accounts/register HTTP/1.1          ← method + đường dẫn
Host: localhost:5080                          ← header
Content-Type: application/json

{ "username": "bang", "password": "secret123" }   ← body

HTTP/1.1 201 Created                          ← status code
Location: /api/players/2880...                ← header
Content-Type: application/json

{ "playerId": "2880...", "username": "bang" }
```

### Method: hành động gì
| Method | Ý nghĩa | Trong project |
|---|---|---|
| GET | Đọc, không thay đổi gì | xem profile, túi đồ |
| POST | Tạo mới / thực hiện một hành động | đăng ký, đăng nhập, nhận thưởng, dùng vật phẩm |
| PUT | Thay thế giá trị | đổi tên hiển thị |
| DELETE | Xóa | (bài tập) |

GET phải **an toàn**: gọi bao nhiêu lần cũng không làm thay đổi dữ liệu. Không bao giờ dùng GET cho "nhận thưởng".

### Status code: kết quả ra sao
| Code | Ý nghĩa | Khi nào trả |
|---|---|---|
| 200 OK | Thành công | hầu hết GET, các hành động thành công |
| 201 Created | Đã tạo tài nguyên mới | đăng ký, kèm header `Location` |
| 400 Bad Request | Dữ liệu gửi lên sai | username quá ngắn, thiếu trường |
| 401 Unauthorized | Chưa xác thực / sai thông tin đăng nhập | đăng nhập sai |
| 404 Not Found | Không có tài nguyên này | `playerId` không tồn tại |
| 409 Conflict | Yêu cầu hợp lệ nhưng xung đột với trạng thái hiện tại | trùng tên, đã nhận thưởng, không có vật phẩm |
| 500 Internal Server Error | Lỗi phía server (bug) | exception chưa bắt |

Quy tắc: **4xx là lỗi của client, 5xx là lỗi của server.** Client Unity dựa vào code để quyết định
hiện thông báo gì, nên phải trả đúng code, không phải lúc nào cũng 200 kèm `{"success": false}`.

### Middleware pipeline
Mỗi request đi qua một chuỗi các bước theo đúng thứ tự trong `Program.cs`, rồi response đi ngược lại:
```
request ──► RequestLogging ──► ExceptionHandler ──► StatusCodePages ──► Endpoint (handler)
response ◄────────────────────────────────────────────────────────────────┘
```
Thứ tự quan trọng: `UseExceptionHandler` phải đứng **trước** các bước có thể ném lỗi thì mới bắt được.

### Minimal API và Controller
ASP.NET Core có hai cách viết endpoint. Project này dùng **Minimal API** (`app.MapGet(...)`), là mặc định
từ .NET 6. Cách cũ hơn là **Controller**, bạn sẽ gặp nhiều trong code có sẵn:
```csharp
[ApiController]
[Route("api/players/{playerId:guid}")]
public class PlayersController(InMemoryDatabase db) : ControllerBase
{
    [HttpGet]
    public ActionResult<PlayerProfileResponse> GetProfile(Guid playerId) { ... }
}
```
Hai cách làm được những việc như nhau. Controller có sẵn validation tự động; Minimal API gọn hơn và nhanh hơn một chút.

### ProblemDetails: một định dạng lỗi cho mọi trường hợp
Mọi lỗi (400, 401, 404, 409, 500) đều trả về cùng một hình dạng JSON (chuẩn RFC 9457):
```json
{ "type": "...", "title": "Daily reward already claimed", "status": 409, "detail": "Come back tomorrow (UTC).", "traceId": "..." }
```
Client chỉ cần viết **một** đoạn code đọc lỗi. `traceId` giúp tìm đúng dòng log của request đó khi người chơi báo lỗi.

### Server là nguồn sự thật
Không có API nào kiểu `PUT /gold { "gold": 999999 }`. Client chỉ được **yêu cầu hành động**
(`POST /daily-reward`), còn server tự kiểm tra điều kiện và tự tính toán. Mọi thứ client gửi lên đều có thể bị sửa
bằng công cụ chặn request, nên không bao giờ tin số liệu client gửi.

### Request song song và `lock`
ASP.NET Core xử lý nhiều request **cùng lúc** trên nhiều thread. Nếu "kiểm tra đã nhận thưởng chưa" và "cộng thưởng"
không nằm trong cùng một `lock`, hai request gửi cùng lúc đều thấy "chưa nhận" và đều được cộng.
Test `DailyReward_ManyParallelRequests_OnlyOneSucceeds` kiểm tra đúng chuyện này.
Khi chuyển sang database ở giai đoạn 3, `lock` trong RAM sẽ được thay bằng transaction của database.

## Bài tập (theo độ khó tăng dần)
1. **Bảng xếp hạng:** `GET /api/leaderboard?top=10` trả về 10 người nhiều vàng nhất. Kiểm tra `top` trong khoảng 1–100.
2. **Xóa tài khoản:** `DELETE /api/players/{playerId}` → 204 No Content. Nhớ xóa cả `Account` lẫn `Player`.
3. **Shop:** `POST /api/players/{playerId}/shop/buy` với body `{ "itemId": "potion", "quantity": 2 }`.
   Giá do **server** quy định (ví dụ potion = 30 vàng). Không đủ vàng thì 409.
4. **Middleware tự viết:** thêm header `X-Server-Time` vào mọi response bằng `app.Use(async (context, next) => ...)`.
5. **Controller:** viết lại nhóm `Players` bằng Controller, so sánh với Minimal API.
6. **Test "sang ngày mới":** cài package `Microsoft.Extensions.TimeProvider.Testing`, thay `TimeProvider` trong test
   bằng `FakeTimeProvider`, tua thời gian sang ngày hôm sau và kiểm tra nhận thưởng được lần nữa.
7. **Unity:** gọi API đăng ký, đăng nhập, xem profile từ Unity bằng `UnityWebRequest` (bước tiếp theo của giai đoạn 2).

## Tự kiểm tra
- Vì sao "nhận thưởng" dùng POST mà không dùng GET?
- Khác nhau giữa 400, 401, 404 và 409? Cho một ví dụ mỗi loại trong game của bạn.
- Vì sao đăng nhập sai tên và sai mật khẩu phải trả về **cùng một** thông báo?
- Vì sao không trả thẳng object `Account` ra JSON mà phải qua `AccountResponse`?
- Bỏ `lock` trong `ClaimDailyReward` thì test nào hỏng, và vì sao?
- `app.UseExceptionHandler()` đặt **sau** `app.MapPlayerEndpoints()` thì có bắt được exception không?
- `InMemoryDatabase` đăng ký là Singleton. Nếu đổi thành Scoped thì chuyện gì xảy ra với dữ liệu?
