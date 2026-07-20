using Vertex.Core.Config;

namespace Vertex.Core.Transport;

/// <summary>
/// Transport-layer abstraction for MQTT connections. Two implementations:
/// <see cref="MqttTlsSocket"/> (TCP + optional TLS) and
/// <see cref="MqttWebSocketSocket"/> (RFC 6455 with subprotocol "mqtt").
/// Mirror of the Swift <c>NWConnection</c> + WS metadata pair and the
/// Kotlin <c>MqttSocket</c> sealed type.
///
/// The framing contract is one MQTT control packet per send call. WS impl
/// turns each call into a single binary frame; TLS impl is byte-streamed
/// (the codec re-frames on the receive side via remaining-length).
/// </summary>
public interface IMqttSocket : IAsyncDisposable
{
    /// <summary>The broker this socket is bound to.</summary>
    BrokerUrl Broker { get; }

    /// <summary>Establish the underlying transport (TCP, TLS handshake, WS upgrade).</summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Send one MQTT control packet. Implementations serialize concurrent calls.</summary>
    Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);

    /// <summary>
    /// Read up to <paramref name="buffer"/>.Length bytes. Returns the number of
    /// bytes actually written into the buffer; 0 means the peer closed cleanly.
    /// Throws on link death (TCP RST, SSL alert, WS abnormal close, etc.).
    /// </summary>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Hard close — no graceful TLS / WS close-frame, see Swift comment in <c>handleDisconnect</c>.</summary>
    Task CloseAsync();
}
