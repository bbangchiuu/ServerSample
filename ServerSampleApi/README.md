# ServerSampleApi

HTTP API cho game, viết bằng ASP.NET Core Minimal API (.NET 9). **Giai đoạn 2** của lộ trình học backend.
Chạy độc lập với TCP server (`../ServerSample`, cổng 7777).

## Chức năng
| Method | Đường dẫn | Mô tả |
|---|---|---|
| POST | `/api/accounts/register` | Đăng ký tài khoản (tạo luôn nhân vật) |
| POST | `/api/accounts/login` | Đăng nhập, trả về `playerId` |
| GET | `/api/players/{playerId}` | Xem profile |
| PUT | `/api/players/{playerId}/display-name` | Đổi tên hiển thị |
| GET | `/api/players/{playerId}/inventory` | Xem túi đồ |
| POST | `/api/players/{playerId}/daily-reward` | Nhận thưởng hằng ngày (100 vàng + 1 `potion`, mỗi ngày UTC một lần) |
| POST | `/api/players/{playerId}/inventory/{itemId}/use` | Dùng 1 vật phẩm |

Giới hạn hiện tại (sẽ xử lý ở các giai đoạn sau):
- Dữ liệu nằm trong RAM, tắt server là mất. Giai đoạn 3 chuyển sang PostgreSQL.
- Các API `/api/players/...` **chưa được bảo vệ**: ai biết `playerId` cũng gọi được. Giai đoạn 4 thêm JWT.

## Cấu trúc
```
src/ServerSample.Api/
  Program.cs                Cấu hình dịch vụ và middleware pipeline
  Accounts/                 Đăng ký, đăng nhập
  Players/                  Profile, túi đồ, thưởng hằng ngày
  Data/                     Account, Player, InMemoryDatabase
  Common/ValidationFilter   Kiểm tra dữ liệu đầu vào ([Required], [StringLength]...)
  ServerSample.Api.http     Các request mẫu để thử trong Visual Studio
tests/ServerSample.Api.Tests/  Integration test (WebApplicationFactory)
docs/phase-2.md             Ghi chú học tập và bài tập
```

## Chạy
```bash
dotnet run --project src/ServerSample.Api
```
- API: http://localhost:5080
- Swagger UI (thử API trên trình duyệt): http://localhost:5080/swagger
- OpenAPI JSON: http://localhost:5080/openapi/v1.json

Test:
```bash
dotnet test
```

Xem [docs/phase-2.md](docs/phase-2.md) để biết giải thích chi tiết và bài tập.
