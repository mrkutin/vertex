using System.Net.WebSockets;
using Vertex.Core.Config;

namespace Vertex.Core.Transport;

/// <summary>
/// MQTT-over-WebSocket transport. Uses subprotocol <c>"mqtt"</c> per
/// MQTT 5.0 §6.4. Each MQTT control packet is sent as a single binary
/// WebSocket frame (matches Mosquitto's WS framing on broker port 443).
/// </summary>
public sealed class MqttWebSocketSocket : IMqttSocket
{
    /// <summary>
    /// Hard upper bound on a single WebSocket message. Mosquitto's broker
    /// config caps MQTT packets at 1700 bytes; even with WS framing
    /// overhead a single binary frame stays well under 4 KiB. 64 KiB
    /// gives us a safety margin while still failing fast on a runaway
    /// or hostile peer trying to drive us into OOM via fragmented frames.
    /// </summary>
    private const int MaxFrameBytes = 64 * 1024;

    /// <summary>
    /// Cap above which we drop the per-message buffer between messages so
    /// it doesn't get pinned in the Large Object Heap (.NET LOH threshold
    /// is 85000 bytes). Routine MQTT control packets are 1–2 KiB so 16 KiB
    /// reuse is plenty.
    /// </summary>
    private const int BufferReuseThreshold = 16 * 1024;

    public BrokerUrl Broker { get; }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private ClientWebSocket? _ws;
    private byte[]? _receiveBuffer;
    private int _receiveLen;
    private int _receivePos;

    public MqttWebSocketSocket(BrokerUrl broker)
    {
        if (!broker.IsWebSocket)
        {
            throw new ArgumentException("MqttWebSocketSocket only handles ws:// and wss:// URLs.", nameof(broker));
        }
        Broker = broker;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("mqtt");
        // KeepAliveInterval=0 disables WS-level pings — MQTT does its own
        // PINGREQ/PINGRESP, double-pinging would waste data and could
        // trigger broker-side rate limits.
        ws.Options.KeepAliveInterval = TimeSpan.Zero;

        var uri = new Uri(Broker.ToString());
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        _ws = ws;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("Socket not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(packet, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // NB: half-closed link detection (broker silent but no Close frame)
        // is the responsibility of MqttConnection's PINGRESP watchdog —
        // socket-layer reads will hang here indefinitely on a stale path.
        var ws = _ws ?? throw new InvalidOperationException("Socket not connected.");

        // We accumulate one WebSocket message into our internal buffer and
        // hand callers byte-stream chunks out of it. This decouples MQTT
        // packet boundaries (set by the codec via remaining-length) from
        // WS frame boundaries (set by Mosquitto, which puts one MQTT
        // packet per binary frame).
        if (_receiveBuffer is null || _receivePos >= _receiveLen)
        {
            await PullNextWebSocketMessageAsync(ws, ct).ConfigureAwait(false);
            if (_receiveLen == 0) return 0; // EOF (close frame received)
        }

        int available = _receiveLen - _receivePos;
        int copy = Math.Min(available, buffer.Length);
        _receiveBuffer.AsMemory(_receivePos, copy).CopyTo(buffer);
        _receivePos += copy;
        return copy;
    }

    private async Task PullNextWebSocketMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        // Drop the previous buffer if it grew past the LOH threshold so we
        // don't pin a large object on the heap forever after a one-off
        // burst (Phase 1.4 reviewer flagged this as a runaway-frame OOM
        // mitigation).
        if (_receiveBuffer is { Length: > BufferReuseThreshold })
        {
            _receiveBuffer = null;
        }
        var buf = _receiveBuffer ?? new byte[4096];
        int written = 0;

        while (true)
        {
            if (written == buf.Length)
            {
                int next = buf.Length * 2;
                if (next > MaxFrameBytes)
                {
                    throw new InvalidOperationException(
                        $"WebSocket message exceeds {MaxFrameBytes}-byte hard cap (received {written} bytes so far). " +
                        "Misbehaving broker or hostile peer.");
                }
                Array.Resize(ref buf, next);
            }

            ValueWebSocketReceiveResult result = await ws.ReceiveAsync(buf.AsMemory(written), ct).ConfigureAwait(false);
            written += result.Count;

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _receiveBuffer = buf;
                _receiveLen = 0;
                _receivePos = 0;
                return;
            }
            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidOperationException(
                    $"Mosquitto WS broker sent unexpected MessageType={result.MessageType}.");
            }
            if (result.EndOfMessage) break;
        }

        _receiveBuffer = buf;
        _receiveLen = written;
        _receivePos = 0;
    }

    public Task CloseAsync()
    {
        try { _ws?.Abort(); } catch { /* swallow */ }
        try { _ws?.Dispose(); } catch { /* swallow */ }
        _ws = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
