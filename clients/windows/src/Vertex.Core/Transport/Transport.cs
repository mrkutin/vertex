namespace Vertex.Core.Transport;

/// <summary>
/// Coarse transport state for UI reporting. Mirrors Swift
/// <c>TransportState</c> and Kotlin <c>TransportState</c>.
/// </summary>
public abstract record TransportState
{
    public sealed record Disconnected : TransportState;
    public sealed record Connecting(string Broker) : TransportState;
    public sealed record Connected(string Broker)  : TransportState;
    public sealed record Reconnecting(string Broker, int Attempt) : TransportState;
}

/// <summary>Errors raised by <see cref="IMqttTransport"/> public methods.</summary>
public sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }

    public static TransportException ConnectTimeout() => new("Transport connect timeout reached.");
    public static TransportException NoBrokers()       => new("No brokers configured.");
}

/// <summary>
/// Abstract pub/sub message transport for the Vertex data plane. Today the
/// only implementation is <see cref="MqttTransport"/>; the contract is
/// minimal so a future NATS / gRPC / QUIC backend can drop in without
/// touching the data-plane.
/// </summary>
public interface IMqttTransport : IAsyncDisposable
{
    /// <summary>True once we received a successful CONNACK on the current broker.</summary>
    bool IsReady { get; }

    /// <summary>Hostname of the currently active broker, or <c>null</c> when disconnected.</summary>
    string? CurrentBroker { get; }

    /// <summary>Async stream of state transitions for the host UI / logger.</summary>
    IAsyncEnumerable<TransportState> StateUpdates { get; }

    /// <summary>Begin connecting; returns when the first CONNACK arrives or the
    /// connect deadline expires (then throws <see cref="TransportException"/>).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Graceful disconnect. Cancels reconnection. Idempotent.</summary>
    Task StopAsync();

    /// <summary>Publish QoS 0. Drops silently if not connected.</summary>
    void Publish(string topic, ReadOnlyMemory<byte> payload);

    /// <summary>Register a handler for messages whose topic matches <paramref name="pattern"/>.
    /// Subscriptions persist across reconnects.</summary>
    void Subscribe(string pattern, Action<string, byte[]> handler);

    /// <summary>Stop dispatching messages for <paramref name="pattern"/> locally.
    /// Does not currently send MQTT UNSUBSCRIBE — broker keeps delivering, dispatch becomes no-op.</summary>
    void Unsubscribe(string pattern);

    /// <summary>Tear down the current connection and reconnect immediately, bypassing backoff.</summary>
    void ForceReconnect(string reason);

    /// <summary>Force a fresh PINGREQ ahead of cadence — used on system wake / NetworkChange hints.</summary>
    void CheckLiveness(string reason);

    /// <summary>Wait until <see cref="IsReady"/> or throw <see cref="TransportException.ConnectTimeout"/>.</summary>
    Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct = default);
}
