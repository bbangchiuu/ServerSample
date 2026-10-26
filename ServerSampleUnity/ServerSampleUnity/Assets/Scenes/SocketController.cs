#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Gắn vào một GameObject trong scene để kết nối tới ServerSample.Server.
/// Có giao diện test đơn giản (OnGUI): ô nhập, nút Chat / Echo, danh sách tin nhắn nhận được.
/// </summary>
public class SocketController : MonoBehaviour
{
    private const int MaxMessages = 15;

    [SerializeField] private string host = "127.0.0.1";
    [SerializeField] private int port = 7777;

    // GameClient gọi sự kiện trên thread nền, mà Unity chỉ cho động vào GameObject/UI trên main thread.
    // Vì vậy sự kiện chỉ XẾP HÀNG vào đây (ConcurrentQueue an toàn với nhiều thread),
    // rồi Update() (main thread) lấy ra xử lý.
    private readonly ConcurrentQueue<CPacket> _incomingPackets = new();
    private readonly ConcurrentQueue<string> _disconnectReasons = new();

    private readonly List<string> _messages = new();
    private GameClient? _client;
    private string _input = "";

    public static SocketController Instance;
    private void Awake()
    {
        Instance = this;
    }
    private async void Start()
    {
        _client = new GameClient();
        _client.PacketReceived += packet => _incomingPackets.Enqueue(packet);
        _client.Disconnected += reason => _disconnectReasons.Enqueue(reason);

        AddMessage($"Connecting to {host}:{port}...");
        try
        {
            await _client.ConnectAsync(host, port, sendHeartbeat: true);
            // Sau await trong Start(), Unity đưa code quay lại main thread, nên gọi AddMessage ở đây là an toàn.
            AddMessage("Connected.");
        }
        catch (Exception ex)
        {
            AddMessage($"Cannot connect: {ex.Message}");
        }
    }

    private void Update()
    {
        while (_incomingPackets.TryDequeue(out CPacket packet))
            HandlePacket(packet);

        while (_disconnectReasons.TryDequeue(out string reason))
            AddMessage($"Disconnected: {reason}");
    }

    private void OnDestroy()
    {
        // Task nền KHÔNG tự dừng khi thoát Play Mode: nếu không đóng ở đây, socket vẫn mở,
        // Ping vẫn gửi, và lần Play sau sẽ có 2 kết nối.
        // Không await được trong OnDestroy, nhưng DisposeAsync đóng socket ngay ở phần đầu (trước await đầu tiên).
        if (_client != null)
            _ = _client.DisposeAsync();
        _client = null;
    }

    private void HandlePacket(CPacket packet)
    {
        switch (packet.Type)
        {
            case MessageType.Pong:
                break; // không hiển thị cho đỡ rối
            case MessageType.EchoReply:
                AddMessage($"[echo] {packet.PayloadAsText}");
                break;
            default:
                AddMessage($"[{packet.Type}] {packet.PayloadAsText}");
                break;
        }
    }

    public async void SendText(MessageType type, string text)
    {
        if (_client == null || !_client.IsConnected)
        {
            AddMessage("Not connected.");
            return;
        }

        try
        {
            await _client.SendAsync(CPacket.FromText(type, text));
        }
        catch (Exception ex)
        {
            AddMessage($"Send failed: {ex.Message}");
        }
    }

    private void AddMessage(string message)
    {
        Debug.Log($"[SocketController] {message}");
        _messages.Add(message);
        if (_messages.Count > MaxMessages)
            _messages.RemoveAt(0);
    }

    //private void OnGUI()
    //{
    //    GUILayout.BeginArea(new Rect(10, 10, 520, 460), GUI.skin.box);

    //    bool online = _client != null && _client.IsConnected;
    //    GUILayout.Label(online ? $"ONLINE  {host}:{port}" : "OFFLINE");

    //    GUILayout.BeginHorizontal();
    //    _input = GUILayout.TextField(_input, GUILayout.Width(330));
    //    if (GUILayout.Button("Chat") && _input.Length > 0)
    //    {
    //        SendText(MessageType.Chat, _input);
    //        _input = "";
    //    }
    //    if (GUILayout.Button("Echo") && _input.Length > 0)
    //    {
    //        SendText(MessageType.Echo, _input);
    //        _input = "";
    //    }
    //    GUILayout.EndHorizontal();

    //    foreach (string message in _messages)
    //        GUILayout.Label(message);

    //    GUILayout.EndArea();
    //}
}
