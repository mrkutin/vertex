using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Config;

namespace Vertex.Core.Transport;

/// <summary>
/// Disconnect cause categories surfaced to <see cref="MqttTransport"/>.
/// Mirrors Swift <c>MQTTConnection.ConnectionEvent</c> and Kotlin
/// <c>MqttConnection.Event</c>.
/// </summary>
public abstract record MqttConnectionEvent
{
    public sealed record Connected : MqttConnectionEvent;

    /// <param name="Cause">Underlying exception, or <c>null</c> for a clean teardown.</param>
    /// <param name="LinkDead">
    ///   <c>true</c> when the underlying network path is gone (PINGRESP timeout,
    ///   socket EBADF / send failure post-CONNACK). The transport escalates to
    ///   a fresh socket on the new default route rather than retrying in-place.
    /// </param>
    /// <param name="ConnackReason">
    ///   Non-null when the broker rejected our CONNECT (any non-zero CONNACK
    ///   reason code; typically 0x86 "Bad username or password" or 0x87
    ///   "Not authorized"). The transport short-circuits the retry loop —
    ///   retrying with the same credentials will keep failing.
    /// </param>
    public sealed record Disconnected(
        Exception? Cause,
        bool LinkDead,
        byte? ConnackReason) : MqttConnectionEvent;
}

/// <summary>
/// One MQTT 5.0 connection on top of an <see cref="IMqttSocket"/>. Drives:
/// CONNECT → CONNACK → ready, PINGREQ on cadence, surfacing state
/// transitions to <see cref="MqttTransport"/> via <see cref="OnEvent"/>.
///
/// NOT responsible for reconnection, broker selection, resubscribe — that's
/// <see cref="MqttTransport"/>'s job (Phase 1.5).
/// </summary>
public sealed class MqttConnection : IAsyncDisposable
{
    /// <summary>Hard upper bound on PINGRESP wait. Combined with the PINGREQ
    /// cadence (<c>keepAlive - 5</c>), caps dead-link detection at ≈keepAlive seconds.</summary>
    private static readonly TimeSpan PingResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly IMqttSocket _socket;
    private readonly string _clientId;
    private readonly string _username;
    private readonly string _password;
    private readonly ushort _keepAliveSeconds;
    private readonly ILogger _log;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _packetIdLock = new();
    private Task? _receiveLoop;
    private Task? _pingLoop;
    private byte[] _receiveBuffer = Array.Empty<byte>();
    private int _nextPacketId = 1;

    /// <summary>
    /// Per-PINGREQ cancellation token. Cancelled when PINGRESP arrives or
    /// when the next ping is sent — ensures a stale watchdog from an
    /// earlier ping cycle can't race against a new one and declare a false
    /// link-dead. Mirrors Swift's <c>pingResponseTimer?.cancel()</c> and
    /// Kotlin's <c>pingResponseTask?.cancel(false)</c>.
    /// </summary>
    private CancellationTokenSource? _pingDeadlineCts;

    private volatile bool _connected;
    private volatile bool _hasBeenReady;

    /// <summary>
    /// Reference-typed handlers go through <c>volatile</c> fields. On ARM64
    /// (Snapdragon X / Surface), without acquire/release semantics a UI
    /// thread setting <c>OnEvent = null</c> could leave the receive loop
    /// invoking a stale delegate on a captured-by-value snapshot from a
    /// different cache line. Kotlin does the same via <c>@Volatile</c>.
    /// </summary>
    private volatile Action<MqttConnectionEvent>? _onEvent;
    private volatile Action<string, byte[]>? _onPublish;

    public BrokerUrl Broker => _socket.Broker;
    public bool IsConnected => _connected;

    /// <summary>Async-safe registration of the publish handler. May be set before <see cref="ConnectAsync"/>.</summary>
    public Action<string, byte[]>? OnPublish { get => _onPublish; set => _onPublish = value; }

    /// <summary>State transition handler. Cleared on teardown to prevent re-entrance.</summary>
    public Action<MqttConnectionEvent>? OnEvent { get => _onEvent; set => _onEvent = value; }

    public MqttConnection(
        IMqttSocket socket,
        string clientId,
        string username,
        string password,
        ushort keepAliveSeconds = 20,
        ILogger? log = null)
    {
        _socket = socket;
        _clientId = clientId;
        _username = username;
        _password = password;
        _keepAliveSeconds = keepAliveSeconds;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Drive the full connect handshake (transport + MQTT CONNECT) and
    /// kick off the receive loop. Returns when the receive loop is
    /// running; CONNACK is awaited asynchronously and reported via
    /// <see cref="OnEvent"/>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _socket.ConnectAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Transport ready; sending MQTT CONNECT to {Host}", Broker.Host);

        var connect = MqttPacketCodec.EncodeConnect(new ConnectPacket(
            ClientId: _clientId,
            Username: string.IsNullOrEmpty(_username) ? null : _username,
            Password: string.IsNullOrEmpty(_password) ? null : _password,
            KeepAlive: _keepAliveSeconds,
            CleanStart: true,
            SessionExpiryInterval: 0));

        try
        {
            await _socket.SendAsync(connect, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await TeardownAsync(ex, linkDead: false, connackReason: null).ConfigureAwait(false);
            throw;
        }

        _receiveLoop = Task.Run(() => RunReceiveLoopAsync(_cts.Token));
    }

    public async Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, bool retain = false, uint? messageExpirySeconds = 10, CancellationToken ct = default)
    {
        if (!_connected) return;

        var pkt = new PublishPacket(topic, payload.ToArray(), retain, messageExpirySeconds);
        try
        {
            await _socket.SendAsync(MqttPacketCodec.EncodePublish(pkt), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Publish send failed — declaring link dead.");
            await TeardownAsync(ex, linkDead: true, connackReason: null).ConfigureAwait(false);
        }
    }

    public async Task SubscribeAsync(IReadOnlyList<string> topics, CancellationToken ct = default)
    {
        if (!_connected || topics.Count == 0) return;

        // Match Swift / Kotlin sequence exactly: 1, 2, …, 0xFFFF, 1, 2, …
        // SUBSCRIBE is rare (join + reconnect resubscribe) so a lock is fine.
        ushort packetId;
        lock (_packetIdLock)
        {
            packetId = (ushort)_nextPacketId;
            _nextPacketId = _nextPacketId == 0xFFFF ? 1 : _nextPacketId + 1;
        }

        try
        {
            await _socket.SendAsync(
                MqttPacketCodec.EncodeSubscribe(new SubscribePacket(packetId, topics)),
                ct).ConfigureAwait(false);
            _log.LogInformation("SUBSCRIBE sent pkt={Pkt} topics=[{Topics}]", packetId, string.Join(",", topics));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Subscribe send failed — declaring link dead.");
            await TeardownAsync(ex, linkDead: true, connackReason: null).ConfigureAwait(false);
        }
    }

    /// <summary>Graceful MQTT DISCONNECT + close socket. Idempotent.</summary>
    public async Task DisconnectAsync()
    {
        if (_connected)
        {
            try { await _socket.SendAsync(MqttPacketCodec.EncodeDisconnect(), CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort */ }
        }
        await TeardownAsync(cause: null, linkDead: false, connackReason: null).ConfigureAwait(false);
    }

    /// <summary>
    /// Force a fresh PINGREQ ahead of the regular cadence. Used when an
    /// external signal (system wake, NetworkChange) suggests the existing
    /// socket may be stale. No-op if disconnected or a ping is already in flight.
    /// </summary>
    public Task PingNowAsync(string reason)
    {
        if (!_connected || _pingDeadlineCts is not null) return Task.CompletedTask;
        _log.LogInformation("PingNow({Reason}): forcing fresh PINGREQ", reason);
        return SendPingOnceAsync();
    }

    // ---- Receive loop ----

    private async Task RunReceiveLoopAsync(CancellationToken ct)
    {
        var rented = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await _socket.ReceiveAsync(rented, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Receive failed");
                    bool linkDead = _hasBeenReady;
                    await TeardownAsync(ex, linkDead, connackReason: null).ConfigureAwait(false);
                    return;
                }

                if (n == 0)
                {
                    // Clean transport-level EOF (TLS close_notify, WS close).
                    // If the peer dropped us *before* CONNACK, surface a
                    // concrete cause so the transport layer's Disconnected
                    // event fires (its single-shot guard would otherwise
                    // swallow a `wasConnected=false && cause=null` teardown
                    // and the upstream Start would hang on its TCS).
                    Exception? cause = _hasBeenReady
                        ? null
                        : new MqttCodecException("Broker closed connection before CONNACK.");
                    await TeardownAsync(cause, linkDead: false, connackReason: null).ConfigureAwait(false);
                    return;
                }

                AppendToReceiveBuffer(rented.AsSpan(0, n));
                await DrainReceiveBufferAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Receive loop crashed");
            await TeardownAsync(ex, linkDead: _hasBeenReady, connackReason: null).ConfigureAwait(false);
        }
    }

    private void AppendToReceiveBuffer(ReadOnlySpan<byte> chunk)
    {
        int existing = _receiveBuffer.Length;
        var grown = new byte[existing + chunk.Length];
        Buffer.BlockCopy(_receiveBuffer, 0, grown, 0, existing);
        chunk.CopyTo(grown.AsSpan(existing));
        _receiveBuffer = grown;
    }

    private async Task DrainReceiveBufferAsync()
    {
        while (true)
        {
            (MqttPacketType type, byte[] packet, int consumed)? decoded;
            try
            {
                decoded = MqttPacketCodec.TryDecode(_receiveBuffer);
            }
            catch (MqttCodecException ex)
            {
                _log.LogError(ex, "Codec error — closing connection");
                await TeardownAsync(ex, linkDead: false, connackReason: null).ConfigureAwait(false);
                return;
            }

            if (decoded is null) break; // need more bytes

            // Slice off the consumed prefix.
            var (type, packet, consumed) = decoded.Value;
            if (consumed == _receiveBuffer.Length)
            {
                _receiveBuffer = Array.Empty<byte>();
            }
            else
            {
                var rest = new byte[_receiveBuffer.Length - consumed];
                Buffer.BlockCopy(_receiveBuffer, consumed, rest, 0, rest.Length);
                _receiveBuffer = rest;
            }

            await HandlePacketAsync(type, packet).ConfigureAwait(false);

            if (!_connected && type != MqttPacketType.Connack)
            {
                // A packet handler triggered teardown (e.g. server DISCONNECT,
                // CONNACK rejected); stop draining so we don't fire a phantom
                // event from the next loop iteration.
                return;
            }
        }
    }

    private async Task HandlePacketAsync(MqttPacketType type, byte[] packet)
    {
        switch (type)
        {
            case MqttPacketType.Connack:
                ConnackPacket ack;
                try { ack = MqttPacketCodec.DecodeConnack(packet); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "CONNACK decode error");
                    await TeardownAsync(ex, linkDead: false, connackReason: null).ConfigureAwait(false);
                    return;
                }

                if (ack.IsSuccess)
                {
                    _log.LogInformation("CONNACK success (sessionPresent={SessionPresent})", ack.SessionPresent);
                    _connected = true;
                    _hasBeenReady = true;
                    StartPingLoop();
                    _onEvent?.Invoke(new MqttConnectionEvent.Connected());
                }
                else
                {
                    _log.LogWarning("CONNACK rejected: {Reason} (code=0x{Code:X2})", ack.ReasonString, ack.ReasonCode);
                    await TeardownAsync(
                        new MqttCodecException($"CONNACK rejected: {ack.ReasonString}"),
                        linkDead: false,
                        connackReason: ack.ReasonCode).ConfigureAwait(false);
                }
                break;

            case MqttPacketType.Publish:
                try
                {
                    var pub = MqttPacketCodec.DecodePublish(packet);
                    _log.LogInformation("PUBLISH recv topic={Topic} bytes={Len}", pub.Topic, pub.Payload.Length);
                    _onPublish?.Invoke(pub.Topic, pub.Payload);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "PUBLISH decode error");
                }
                break;

            case MqttPacketType.Suback:
                try
                {
                    var sub = MqttPacketCodec.DecodeSuback(packet);
                    _log.LogInformation("SUBACK recv pkt={Pkt} codes=[{Codes}] all_ok={Ok}",
                        sub.PacketId,
                        string.Join(",", sub.ReasonCodes.Select(c => $"0x{c:X2}")),
                        sub.AllSuccess);
                    if (!sub.AllSuccess)
                    {
                        _log.LogWarning("SUBACK partial failure: codes=[{Codes}]",
                            string.Join(",", sub.ReasonCodes.Select(c => $"0x{c:X2}")));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "SUBACK decode error");
                }
                break;

            case MqttPacketType.Pingresp:
                // Cancel the per-ping watchdog so a stale Task.Delay can't
                // fire after a delayed PINGRESP and declare false link-death.
                try { _pingDeadlineCts?.Cancel(); } catch { /* swallow */ }
                _pingDeadlineCts = null;
                break;

            case MqttPacketType.Disconnect:
                _log.LogInformation("Received DISCONNECT from broker");
                await TeardownAsync(cause: null, linkDead: false, connackReason: null).ConfigureAwait(false);
                break;
        }
    }

    // ---- Ping loop ----

    private void StartPingLoop()
    {
        var interval = TimeSpan.FromSeconds(Math.Max(_keepAliveSeconds - 5, 5));
        _pingLoop = Task.Run(() => RunPingLoopAsync(interval, _cts.Token));
    }

    private async Task RunPingLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_connected) return;
                await TickPingAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
    }

    private async Task TickPingAsync()
    {
        if (_pingDeadlineCts is not null)
        {
            // The previous ping watchdog is still scheduled. It will fire
            // independently if PINGRESP doesn't arrive in time — defence
            // in depth: if for whatever reason that watchdog missed, we
            // declare link dead now rather than continuing to ping into a
            // black hole.
            _log.LogWarning("PINGRESP still pending at next PINGREQ — declaring link dead");
            await TeardownAsync(
                new MqttCodecException("PINGRESP timeout"),
                linkDead: true,
                connackReason: null).ConfigureAwait(false);
            return;
        }

        await SendPingOnceAsync().ConfigureAwait(false);
    }

    private async Task SendPingOnceAsync()
    {
        // Cancel any earlier watchdog (defence-in-depth — TickPing already
        // bails when one is active, but PingNowAsync may slip past on a
        // path-change signal) and arm a fresh one for this ping.
        try { _pingDeadlineCts?.Cancel(); } catch { /* swallow */ }
        var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _pingDeadlineCts = deadlineCts;

        try
        {
            await _socket.SendAsync(MqttPacketCodec.EncodePingReq(), _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PINGREQ send failed — declaring link dead");
            await TeardownAsync(ex, linkDead: true, connackReason: null).ConfigureAwait(false);
            return;
        }

        // Independent watchdog: if PINGRESP doesn't arrive within
        // PingResponseTimeout, the link is dead — even if NWConnection /
        // Stream-level health hasn't noticed yet (notoriously slow on
        // Wi-Fi disconnect on Windows where the radio stays on).
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PingResponseTimeout, deadlineCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            // We arrived here without being cancelled — PINGRESP didn't
            // make it back. Reference equality check: if the connection
            // already moved on to a fresh watchdog, this stale one stays
            // silent.
            if (ReferenceEquals(_pingDeadlineCts, deadlineCts))
            {
                _log.LogWarning("PINGRESP not received within {Timeout}s — link dead", (int)PingResponseTimeout.TotalSeconds);
                await TeardownAsync(
                    new MqttCodecException("PINGRESP timeout"),
                    linkDead: true,
                    connackReason: null).ConfigureAwait(false);
            }
        });
    }

    // ---- Teardown ----

    private int _teardownDone;

    private async Task TeardownAsync(Exception? cause, bool linkDead, byte? connackReason)
    {
        // Single-shot: only the first caller emits the Disconnected event.
        if (Interlocked.Exchange(ref _teardownDone, 1) != 0) return;

        bool wasConnected = _connected;
        _connected = false;
        try { _pingDeadlineCts?.Cancel(); } catch { /* swallow */ }
        _pingDeadlineCts = null;

        try { _cts.Cancel(); } catch { /* swallow */ }
        try { await _socket.CloseAsync().ConfigureAwait(false); } catch { /* swallow */ }

        // Capture and null the handler atomically so a late callback (a
        // ping watchdog firing between our Cancel and the socket close)
        // can't re-emit Disconnected.
        var handler = _onEvent;
        _onEvent = null;
        _onPublish = null;

        if (wasConnected || cause != null)
        {
            handler?.Invoke(new MqttConnectionEvent.Disconnected(cause, linkDead, connackReason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);

        try { if (_receiveLoop is not null) await _receiveLoop.ConfigureAwait(false); } catch { }
        try { if (_pingLoop    is not null) await _pingLoop.ConfigureAwait(false); }    catch { }

        await _socket.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
