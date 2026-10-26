# ServerSample

Dự án học backend cho game bằng C# / .NET, đi theo lộ trình 8 giai đoạn.
Hiện tại: **Giai đoạn 0–1**, gồm TCP socket server với length-prefix framing, heartbeat, chat broadcast và bot load test.

## Cấu trúc

```
src/
  ServerSample.Shared/   Giao thức dùng chung server–client: MessageType, Packet, PacketSerializer
  ServerSample.Server/   TCP server (Generic Host, DI, Options, Serilog)
    Networking/
      TcpServerService   Accept loop, nhận kết nối mới
      ClientSession      Một client: vòng nhận + vòng gửi (Channel)
      SessionManager     Danh sách client online (ConcurrentDictionary)
      PacketDispatcher   Xử lý từng loại gói
      HeartbeatMonitor   Ngắt client không gửi Ping
  ServerSample.Client/   Client console: chat tương tác, --bots, --silent
tests/
  ServerSample.Tests/    Unit test cho PacketSerializer
docs/                    Ghi chú học tập
docker-compose.yml       Postgres + Redis (dùng từ giai đoạn 3)
```

## Chạy

```bash
dotnet build
dotnet test
```

Mở 3 terminal:

```bash
dotnet run --project src/ServerSample.Server
```

```bash
dotnet run --project src/ServerSample.Client
```

```bash
dotnet run --project src/ServerSample.Client -- --bots 200
```

Client tương tác: gõ chữ để chat, `/echo <text>` để echo, `/quit` để thoát.
`--silent` kết nối nhưng không gửi Ping, server sẽ ngắt sau ~15 giây.

Cấu hình server (cổng, heartbeat...) nằm trong `src/ServerSample.Server/ServerOptions.cs`, cấu hình log nằm trong `Program.cs`. Đổi xong phải build lại.
## Giao thức

```
[ 4 byte: độ dài payload, int32 little-endian ][ 1 byte: MessageType ][ payload ]
```

Xem [docs/phase-0-1.md](docs/phase-0-1.md) để biết giải thích chi tiết và bài tập.
