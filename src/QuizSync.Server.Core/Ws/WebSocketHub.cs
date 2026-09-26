using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Ws;

/// <summary>
/// WebSocket 事件推送中枢（`spec/05-websocket.md`）。
///
/// 与 v1 一致的口径：
/// - **每个 device_id 只保留一条连接**：同一台设备再连一次，旧的会被关掉；
/// - 事件是**单向**的（服务端 → 客户端）；客户端发来的 `hello`/`pong`/`ack`/`push_ops`
///   只被读取，不做业务（op 推送走 HTTP）；
/// - 服务端**从不**发 `ops` 事件（客户端那条分支是死分支，向量有负向断言钉住）。
/// </summary>
public sealed class WebSocketHub
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, WebSocket> _connections = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ConnectedDeviceIds => _connections.Keys.ToList();

    /// <summary>登记一条连接并**踢掉同一设备的旧连接**。</summary>
    public async Task AddAsync(string deviceId, WebSocket socket)
    {
        if (_connections.TryRemove(deviceId, out var previous))
        {
            await CloseAsync(previous, "同一设备的新连接已建立").ConfigureAwait(false);
        }

        _connections[deviceId] = socket;
    }

    public void Remove(string deviceId, WebSocket socket)
    {
        // 迟到的 onDone 不能把新连接误删（v1 的同款防护）。
        if (_connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current, socket))
        {
            _connections.TryRemove(deviceId, out _);
        }
    }

    public void Send(string deviceId, JsonObject message)
    {
        if (_connections.TryGetValue(deviceId, out var socket))
        {
            _ = SendAsync(socket, message);
        }
    }

    public void Broadcast(JsonObject message)
    {
        foreach (var socket in _connections.Values)
        {
            _ = SendAsync(socket, message);
        }
    }

    /// <summary>
    /// 广播并**等所有帧发完**。需要「先广播再关连接」的顺序时用它
    /// （吊销设备就是：先给 device_revoked，再关连接；用 fire-and-forget 会抢跑）。
    /// </summary>
    public async Task BroadcastAsync(JsonObject message)
    {
        foreach (var socket in _connections.Values.ToList())
        {
            await SendAsync(socket, message).ConfigureAwait(false);
        }
    }

    /// <summary>关掉某台设备的连接（吊销设备后调用）。</summary>
    public async Task CloseForAsync(string deviceId)
    {
        if (_connections.TryRemove(deviceId, out var socket))
        {
            await CloseAsync(socket, "设备已被吊销").ConfigureAwait(false);
        }
    }

    private static async Task SendAsync(WebSocket socket, JsonObject message)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // 对端已经断了：连接会在读循环里被清掉。
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // 关闭握手要等对端回帧 —— **给个上限**：客户端不回应时不能让
                // 调用方（例如吊销设备的那个 HTTP 请求）一直挂着。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // 已经关了，或者对端没回应：到此为止。
        }
    }
}
