import Foundation

/// Abstract message transport for the Vertex data plane.
///
/// Concrete implementations live alongside this file (today: `MQTTTransport`;
/// future candidates: NATS, gRPC bidi stream, QUIC datagram). The protocol
/// is intentionally minimal — universally-supported pub/sub primitives only.
/// Capabilities that vary by transport (retained messages, last-will,
/// schema validation) sit in separate protocols (e.g. `Retainer`) so that
/// consumers needing a capability assert it explicitly:
///
/// ```swift
/// if let r = transport as? Retainer {
///     r.publishRetained(topic: ..., payload: ...)
/// }
/// ```
///
/// On Apple platforms we use class-bound protocol existentials so the
/// runtime cost stays low (no boxing for value-type implementations because
/// there are none in scope).
public protocol Transport: AnyObject, Sendable {
    /// Start connecting. Returns when the first CONNACK arrives, or throws
    /// on timeout / authentication failure.
    func start() async throws

    /// Graceful disconnect. Cancels any pending reconnection.
    func stop()

    /// Publish a message to the given topic. Fire-and-forget — drops
    /// silently when not connected (caller is expected to retry via the
    /// application protocol if reliability is needed; the data plane uses
    /// QoS 0 semantics).
    func publish(topic: String, payload: Data)

    /// Register a handler for messages whose topic matches the pattern.
    /// Pattern semantics are transport-defined (MQTT wildcards `+`/`#` for
    /// the MQTT impl). Subscriptions are persisted across reconnects.
    func subscribe(pattern: String, handler: @escaping @Sendable (String, Data) -> Void)

    /// Wait until the transport reaches the connected state, or `timeout`
    /// elapses (then throws `TransportError.connectTimeout`).
    func waitReady(timeout: Duration) async throws

    /// Tear down the underlying connection and reconnect immediately,
    /// bypassing the backoff schedule. Implementations should debounce
    /// rapid successive calls. Currently used internally by MQTT
    /// implementations when application-level keepalive (PINGRESP
    /// timeout) detects a dead link.
    func forceReconnect(reason: String)

    /// True when the transport is connected and operational.
    var isReady: Bool { get }

    /// Hostname of the broker we're currently connected to, or nil.
    var currentBroker: String? { get }

    /// Async stream of state transitions, for UI reporting.
    var stateUpdates: AsyncStream<TransportState> { get }
}

/// Optional capability for transports that support state-style messages
/// where new subscribers receive the last value published on the topic.
/// MQTT supports it natively (retained); NATS Core does not (use JetStream
/// KV); WebRTC has no equivalent. No Swift consumer publishes retained
/// today — this protocol exists for symmetry with the Go side and future
/// in-process exit implementations.
public protocol Retainer: AnyObject, Sendable {
    func publishRetained(topic: String, payload: Data)
}

/// Connection state for UI reporting. Mirrors the Go-side states.
public enum TransportState: Sendable {
    case disconnected
    case connecting(broker: String)
    case connected(broker: String)
    case reconnecting(broker: String, attempt: Int)
}

public enum TransportError: Error, Sendable {
    case connectTimeout
    case noBrokers
}
