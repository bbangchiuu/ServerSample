using System.Diagnostics;
using ServerSample.Client;
using ServerSample.Shared;

// Cách dùng:
//   dotnet run --project src/ServerSample.Client                    -> chat tương tác
//   dotnet run --project src/ServerSample.Client -- --bots 200      -> 200 bot gửi Echo, đo độ trễ
//   dotnet run --project src/ServerSample.Client -- --silent        -> không gửi Ping, sẽ bị server ngắt sau ~15s
//   Thêm --host <ip> --port <port> để kết nối server khác.

string host = GetArg("--host") ?? "127.0.0.1";
int port = int.Parse(GetArg("--port") ?? "7777");
int bots = int.Parse(GetArg("--bots") ?? "0");
bool silent = args.Contains("--silent");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Chế độ bot: không kill process ngay, để in thống kê và dọn dẹp.
    // Chế độ chat: Console.ReadLine đang chặn, nên cứ để Ctrl+C thoát luôn.
    e.Cancel = bots > 0;
    cts.Cancel();
};

if (bots > 0)
    await BotRunner.RunAsync(host, port, bots, cts.Token);
else
    await RunInteractiveAsync(host, port, sendHeartbeat: !silent, cts.Token);

return;

string? GetArg(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static async Task RunInteractiveAsync(string host, int port, bool sendHeartbeat, CancellationToken ct)
{
    await using var client = new GameClient();
    client.PacketReceived += packet =>
    {
        switch (packet.Type)
        {
            case MessageType.Pong:
                break; // không in, để console đỡ rối
            case MessageType.EchoReply:
                Console.WriteLine($"[echo] {packet.PayloadAsText}");
                break;
            default:
                Console.WriteLine($"[{packet.Type}] {packet.PayloadAsText}");
                break;
        }
    };
    client.Disconnected += reason => Console.WriteLine($"*** Disconnected: {reason}. Press Enter to exit.");

    await client.ConnectAsync(host, port, sendHeartbeat, ct);
    Console.WriteLine($"Connected to {host}:{port}{(sendHeartbeat ? "" : " (silent mode: no heartbeat)")}");
    Console.WriteLine("Type to chat, '/echo <text>' to echo, '/quit' to exit.");

    while (!ct.IsCancellationRequested && client.IsConnected)
    {
        string? line = Console.ReadLine();
        if (line is null || line == "/quit" || !client.IsConnected)
            break;

        CPacket packet = line.StartsWith("/echo ", StringComparison.Ordinal)
            ? CPacket.FromText(MessageType.Echo, line["/echo ".Length..])
            : CPacket.FromText(MessageType.Chat, line);
        await client.SendAsync(packet, ct);
    }
}

/// <summary>
/// Giả lập nhiều client cùng lúc. Bài tập về Task: N bot chạy song song mà không cần N thread.
/// </summary>
static class BotRunner
{
    public static async Task RunAsync(string host, int port, int count, CancellationToken ct)
    {
        long echoCount = 0;
        long latencyTicks = 0;
        int online = 0;

        async Task RunBotAsync()
        {
            await using var client = new GameClient();
            client.PacketReceived += packet =>
            {
                if (packet.Type != MessageType.EchoReply)
                    return;
                // Payload là thời điểm gửi, nên độ trễ = bây giờ - lúc gửi (round-trip time).
                TimeSpan rtt = Stopwatch.GetElapsedTime(long.Parse(packet.PayloadAsText));
                Interlocked.Add(ref latencyTicks, rtt.Ticks);
                Interlocked.Increment(ref echoCount);
            };

            bool counted = false;
            try
            {
                await client.ConnectAsync(host, port, sendHeartbeat: true, ct);
                Interlocked.Increment(ref online);
                counted = true;

                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    await client.SendAsync(CPacket.FromText(MessageType.Echo, Stopwatch.GetTimestamp().ToString()), ct);
                    await Task.Delay(1000 + Random.Shared.Next(-200, 200), ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bot error: {ex.Message}");
            }
            finally
            {
                if (counted)
                    Interlocked.Decrement(ref online);
            }
        }

        Console.WriteLine($"Starting {count} bots against {host}:{port}. Ctrl+C to stop.");
        Task[] botTasks = Enumerable.Range(0, count).Select(_ => RunBotAsync()).ToArray();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                long echoes = Interlocked.Exchange(ref echoCount, 0);
                long ticks = Interlocked.Exchange(ref latencyTicks, 0);
                double avgMs = echoes == 0 ? 0 : TimeSpan.FromTicks(ticks / echoes).TotalMilliseconds;
                Console.WriteLine($"online: {Volatile.Read(ref online),5} | echo/s: {echoes / 5.0,8:N1} | avg RTT: {avgMs,6:N2} ms");
            }
        }
        catch (OperationCanceledException)
        {
        }

        await Task.WhenAll(botTasks);
        Console.WriteLine("All bots stopped.");
    }
}
