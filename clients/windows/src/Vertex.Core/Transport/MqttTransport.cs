using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Config;
using Vertex.Core.Protocol;

namespace Vertex.Core.Transport;

/// <summary>
/// MQTT 5.0 transport with multi-broker failover, sticky reconnect, and
/// per-attempt connect timeout. Mirror of Swift <c>MQTTTransport</c> and
/// Kotlin <c>MqttTransport</c>.
///
/// Liveness model: the only signal we trust is application-level keepalive
/// (PINGRESP timeout from <see cref="MqttConnection"/>) — every other system
/// network API on Apple / Android was unreliable on at least one real-device
/// scenario, and Windows is no different. On link-dead we reconnect
/// in-place (same approach as Kotlin: Windows Service has no on-demand
/// equivalent of iOS NEPacketTunnelProvider's auto-restart).
///
/// Threading: every state mutation is serialized through a
/// <see cref="SemaphoreSlim"/> queue. Public methods marshal onto it; the
/// <see cref="StateUpdates"/> channel can be consumed from any thread.
/// </summary>
public sealed class MqttTransport : IMqttTransport
{
    private const int ConnectTimeoutSeconds = 15;
    private static readonly double[] BackoffDelaysSec = { 0.0, 0.5, 1.0, 2.0, 5.0 };
    private const int FatalAfterConsecutiveTimeouts = 3;

    private readonly string _username;
    private readonly string _password;
    private readonly string _clientId;
    private readonly ushort _keepAliveSeconds;
    private readonly Func<BrokerUrl, IMqttSocket> _socketFactory;
    private readonly Action<string>? _onFatalError;
    private readonly Action<byte, string>? _onAuthFailure;
    private readonly ILogger _log;

    private readonly List<BrokerUrl> _brokers;

    /// <summary>
    /// Pattern → handler map. Map (not list of pairs) so a second
    /// <see cref="Subscribe"/> for the same pattern replaces the handler
    /// instead of stacking — otherwise dispatch would fan a single broker
    /// payload out to every accumulated handler. Insertion order is
    /// preserved for deterministic dispatch ordering.
    ///
    /// Cross-platform note: Kotlin's <c>LinkedHashMap</c> matches; the
    /// current Swift reference (<c>MQTTTransport.swift</c>) still uses
    /// <c>[(String, Handler)]</c> and has the duplicate-handler bug —
    /// see Phase 1.5 review MAJ-6 (separate fix tracked outside this repo).
    /// </summary>
    private readonly Dictionary<string, Action<string, byte[]>> _subscriptions = new();
    private readonly object _subscriptionsLock = new();

    /// <summary>
    /// Single FIFO work-queue. Every state-mutating public method posts a
    /// body here and one consumer (<see cref="RunWorkLoopAsync"/>) drains
    /// it serially — same semantics as Swift <c>queue.async</c> and Kotlin
    /// single-threaded <c>ScheduledExecutorService</c>.
    /// </summary>
    private readonly Channel<Func<Task>> _workQueue = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Broadcast subscribers for <see cref="StateUpdates"/>. Each call to
    /// the property creates a fresh per-subscriber channel; <see cref="EmitState"/>
    /// fans out to every live subscriber. Cached <see cref="_lastState"/>
    /// is replayed on subscribe so a late host gets the snapshot
    /// (paritет с Kotlin <c>StateFlow</c> replay=1).
    /// </summary>
    private readonly object _subscribersLock = new();
    private readonly List<Channel<TransportState>> _subscribers = new();
    private TransportState _lastState = new TransportState.Disconnected();

    private Task? _workLoop;

    private MqttConnection? _connection;
    private bool _shouldReconnect;

    // Volatile so IsReady / CurrentBroker getters can read without holding
    // the work-queue (worker thread is the sole writer, getters can race
    // freely on x64/ARM64 — paritет с Kotlin `@Volatile`).
    private volatile bool _isReady;

    private int _currentBrokerIndex;
    private int _reconnectAttempt;
    private int _consecutiveConnectFailures;
    private CancellationTokenSource? _connectTimeoutCts;
    private CancellationTokenSource? _reconnectCts;
    private TaskCompletionSource? _readyTcs;
    private int _disposed;

    public bool IsReady => _isReady;

    public string? CurrentBroker
    {
        get
        {
            // Snapshot semantics: torn-read on the int index is harmless
            // because we gate on `_isReady` first, and `_brokers[i].Host`
            // is an immutable `BrokerUrl` record. The race window is "just
            // demoted index" → return either old or new winner host, both
            // valid answers.
            if (!_isReady) return null;
            int idx = _currentBrokerIndex;
            return idx < _brokers.Count ? _brokers[idx].Host : null;
        }
    }

    public IAsyncEnumerable<TransportState> StateUpdates => CreateSubscriberAsync();

    public MqttTransport(
        IReadOnlyList<BrokerUrl> brokers,
        string username,
        string password,
        string clientId,
        ushort keepAliveSeconds = 20,
        Func<BrokerUrl, IMqttSocket>? socketFactory = null,
        Action<string>? onFatalError = null,
        Action<byte, string>? onAuthFailure = null,
        ILogger? log = null)
    {
        if (brokers is null || brokers.Count == 0)
        {
            throw TransportException.NoBrokers();
        }

        _brokers = brokers.ToList();
        _username = username;
        _password = password;
        _clientId = clientId;
        _keepAliveSeconds = keepAliveSeconds;
        _socketFactory = socketFactory ?? DefaultSocketFor;
        _onFatalError = onFatalError;
        _onAuthFailure = onAuthFailure;
        _log = log ?? NullLogger.Instance;

        _workLoop = Task.Run(RunWorkLoopAsync);
    }

    private static IMqttSocket DefaultSocketFor(BrokerUrl b) =>
        b.IsWebSocket ? new MqttWebSocketSocket(b) : new MqttTlsSocket(b);

    // ---- Public API ----

    public Task StartAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(async () =>
        {
            // Re-entrant Start: surface the new caller's TCS to the same
            // outcome as the in-flight start so we don't leak a Pending
            // continuation. The previous caller's TCS gets the same fate
            // it would have got via the connect path.
            if (_readyTcs is { } prev)
            {
                prev.TrySetException(new TransportException(
                    "StartAsync called again before previous start completed"));
            }
            _shouldReconnect = true;
            _readyTcs = tcs;
            await ConnectToCurrentBrokerAsync().ConfigureAwait(false);
        });
        return ct.CanBeCanceled
            ? tcs.Task.WaitAsync(ct)
            : tcs.Task;
    }

    public Task StopAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(async () =>
        {
            _shouldReconnect = false;
            CancelAllTimers();
            if (_connection is { } c) { try { await c.DisconnectAsync().ConfigureAwait(false); } catch { } }
            _connection = null;
            _isReady = false;
            _readyTcs?.TrySetCanceled();
            _readyTcs = null;
            EmitState(new TransportState.Disconnected());
            done.TrySetResult();
        });
        return done.Task;
    }

    public void Publish(string topic, ReadOnlyMemory<byte> payload)
    {
        // Capture by value before scheduling; payload buffer caller-owned semantics
        // match Swift / Kotlin.
        var copy = payload.ToArray();
        Schedule(async () =>
        {
            if (_connection is { } c && _isReady)
            {
                await c.PublishAsync(topic, copy, retain: false, messageExpirySeconds: 10).ConfigureAwait(false);
            }
        });
    }

    public void Subscribe(string pattern, Action<string, byte[]> handler)
    {
        Schedule(async () =>
        {
            bool isNew;
            lock (_subscriptionsLock)
            {
                isNew = !_subscriptions.ContainsKey(pattern);
                _subscriptions[pattern] = handler;
            }

            // Only ask the broker the first time we see the pattern; a
            // re-subscribe is harmless on MQTT 5 but a list-based local
            // map could double-dispatch. We use a Map, but be conservative.
            if (_isReady && isNew && _connection is { } c)
            {
                await c.SubscribeAsync(new[] { pattern }).ConfigureAwait(false);
            }
        });
    }

    public void Unsubscribe(string pattern)
    {
        Schedule(() =>
        {
            lock (_subscriptionsLock) { _subscriptions.Remove(pattern); }
            return Task.CompletedTask;
        });
    }

    public void ForceReconnect(string reason)
    {
        Schedule(async () =>
        {
            if (!_shouldReconnect) return;
            _log.LogInformation("Force reconnect: {Reason}", reason);
            _isReady = false;
            if (_connection is { } c) { try { await c.DisconnectAsync().ConfigureAwait(false); } catch { } }
            _connection = null;
            _reconnectAttempt = 0;
            _consecutiveConnectFailures = 0;
            CancelAllTimers();
            EmitState(new TransportState.Reconnecting(_brokers[_currentBrokerIndex].Host, 0));
            await ConnectToCurrentBrokerAsync().ConfigureAwait(false);
        });
    }

    public void CheckLiveness(string reason)
    {
        Schedule(async () =>
        {
            if (!_isReady || _connection is null) return;
            await _connection.PingNowAsync(reason).ConfigureAwait(false);
        });
    }

    public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (IsReady) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await foreach (var state in StateUpdates.WithCancellation(cts.Token).ConfigureAwait(false))
            {
                if (state is TransportState.Connected) return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TransportException.ConnectTimeout();
        }

        // Channel closed without ever hitting Connected — surface as timeout.
        throw TransportException.ConnectTimeout();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { await StopAsync().ConfigureAwait(false); } catch { /* swallow */ }

        try { _cts.Cancel(); } catch { /* swallow */ }
        _workQueue.Writer.TryComplete();

        if (_workLoop is { } loop)
        {
            try { await loop.ConfigureAwait(false); } catch { /* swallow */ }
        }

        // Snapshot subscribers, complete each — late iterators see the
        // channel close cleanly.
        Channel<TransportState>[] subs;
        lock (_subscribersLock) { subs = _subscribers.ToArray(); _subscribers.Clear(); }
        foreach (var s in subs) s.Writer.TryComplete();

        // Don't Dispose _cts here — managed-only resource, GC handles it.
        // Disposing races with already-queued work bodies still in flight.
    }

    // ---- Serialization ----

    /// <summary>
    /// Post <paramref name="work"/> onto the single FIFO worker. Mirrors
    /// Swift <c>queue.async { … }</c> and Kotlin <c>scheduler.execute</c>:
    /// fire-and-forget, but ordered. The single consumer guarantees no
    /// two bodies execute concurrently — getter-side state can be read
    /// without locks because writers are sequenced.
    /// </summary>
    private void Schedule(Func<Task> work) => _workQueue.Writer.TryWrite(work);

    private async Task RunWorkLoopAsync()
    {
        try
        {
            await foreach (var work in _workQueue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Scheduled transport work failed");
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    // ---- State broadcast ----

    private async IAsyncEnumerable<TransportState> CreateSubscriberAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateUnbounded<TransportState>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        TransportState replay;
        lock (_subscribersLock)
        {
            _subscribers.Add(ch);
            replay = _lastState;
        }

        // Emit the cached latest state so a late subscriber sees the
        // current snapshot — paritет с Kotlin StateFlow replay=1.
        yield return replay;

        try
        {
            await foreach (var s in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return s;
            }
        }
        finally
        {
            lock (_subscribersLock) { _subscribers.Remove(ch); }
            ch.Writer.TryComplete();
        }
    }

    // ---- Connection lifecycle ----

    private async Task ConnectToCurrentBrokerAsync()
    {
        if (!_shouldReconnect) return;

        var broker = _brokers[_currentBrokerIndex];
        _log.LogInformation("Connecting to {Host}:{Port} ({Scheme})", broker.Host, broker.Port, broker.Scheme);
        EmitState(new TransportState.Connecting(broker.Host));

        IMqttSocket socket;
        MqttConnection conn;
        try
        {
            socket = _socketFactory(broker);
            conn = new MqttConnection(socket, _clientId, _username, _password, _keepAliveSeconds, _log);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Socket factory threw for {Host}", broker.Host);
            await HandleConnectionEventAsync(
                new MqttConnectionEvent.Disconnected(ex, LinkDead: false, ConnackReason: null)).ConfigureAwait(false);
            return;
        }

        conn.OnEvent = ev => Schedule(() => HandleConnectionEventAsync(ev));
        conn.OnPublish = (topic, payload) => Dispatch(topic, payload);
        _connection = conn;

        ScheduleConnectTimeout();

        try
        {
            await conn.ConnectAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Connect to {Host} failed", broker.Host);
            // Treat as a Disconnected event so the standard handler picks
            // backoff / sticky / fatal escalation in one place.
            await HandleConnectionEventAsync(
                new MqttConnectionEvent.Disconnected(ex, LinkDead: false, ConnackReason: null)).ConfigureAwait(false);
        }
    }

    private async Task HandleConnectionEventAsync(MqttConnectionEvent ev)
    {
        switch (ev)
        {
            case MqttConnectionEvent.Connected:
                CancelConnectTimeout();
                _log.LogInformation("MQTT connected to {Host}", _brokers[_currentBrokerIndex].Host);
                _isReady = true;
                _reconnectAttempt = 0;
                _consecutiveConnectFailures = 0;

                // Sticky reconnect: move the winning broker to position 0
                // so the next reconnect prefers it.
                if (_currentBrokerIndex > 0)
                {
                    var winner = _brokers[_currentBrokerIndex];
                    _brokers.RemoveAt(_currentBrokerIndex);
                    _brokers.Insert(0, winner);
                    _currentBrokerIndex = 0;
                }

                // Resubscribe everything in our local map.
                List<string> patterns;
                lock (_subscriptionsLock) { patterns = _subscriptions.Keys.ToList(); }
                if (patterns.Count > 0 && _connection is { } cc)
                {
                    await cc.SubscribeAsync(patterns).ConfigureAwait(false);
                    _log.LogInformation("Resubscribed {Count} topics", patterns.Count);
                }

                EmitState(new TransportState.Connected(_brokers[0].Host));
                _readyTcs?.TrySetResult();
                _readyTcs = null;
                break;

            case MqttConnectionEvent.Disconnected dc:
                CancelConnectTimeout();
                bool wasReady = _isReady;
                _isReady = false;
                _connection = null;

                if (dc.Cause is not null)
                {
                    _log.LogWarning(dc.Cause,
                        "MQTT disconnected linkDead={LinkDead} connackReason={ConnackReason}",
                        dc.LinkDead, dc.ConnackReason);
                }
                else
                {
                    _log.LogInformation("MQTT disconnected linkDead={LinkDead}", dc.LinkDead);
                }

                // Auth / CONNACK rejection — same creds will keep failing.
                if (dc.ConnackReason is byte rc && rc != 0)
                {
                    _log.LogWarning("CONNACK rejected (code=0x{Code:X2}) — escalating, no retry", rc);

                    // Un-sticky: a previous successful connect promoted this
                    // broker to index 0; auth-rejecting now means it's
                    // misconfigured (creds rotated, ACL changed, …). Demote
                    // it so a future StartAsync tries the original primary
                    // first — the user shouldn't be locked out by one bad
                    // broker once another is healthy. Same fix recommended
                    // for Swift / Kotlin (review note MAJ-5).
                    if (_brokers.Count > 1 && _currentBrokerIndex == 0)
                    {
                        var loser = _brokers[0];
                        _brokers.RemoveAt(0);
                        _brokers.Add(loser);
                    }

                    _shouldReconnect = false;
                    CancelAllTimers();
                    _readyTcs?.TrySetException(new TransportException(
                        $"CONNACK rejected with reason 0x{rc:X2}"));
                    _readyTcs = null;
                    _onAuthFailure?.Invoke(rc, ConnackReasonString(rc));
                    return;
                }

                // Link-dead: reconnect in-place. Windows Service has no
                // on-demand restart equivalent of iOS extension reload, so
                // we don't escalate to fatal here (mirrors Kotlin).
                if (dc.LinkDead)
                {
                    _log.LogInformation("Link dead — reconnecting in place");
                }

                if (wasReady)
                {
                    EmitState(new TransportState.Reconnecting(
                        _brokers[_currentBrokerIndex].Host, _reconnectAttempt));
                }

                ScheduleReconnect();
                break;
        }
    }

    private void ScheduleConnectTimeout()
    {
        CancelConnectTimeout();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _connectTimeoutCts = cts;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(ConnectTimeoutSeconds), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            Schedule(async () =>
            {
                if (!ReferenceEquals(_connectTimeoutCts, cts)) return;
                _connectTimeoutCts = null;
                if (_isReady || !_shouldReconnect) return;

                _consecutiveConnectFailures++;
                _log.LogWarning("Connect timeout — aborting ({Failures} consecutive)", _consecutiveConnectFailures);

                if (_connection is { } c) { try { await c.DisconnectAsync().ConfigureAwait(false); } catch { } }
                _connection = null;

                if (_consecutiveConnectFailures >= FatalAfterConsecutiveTimeouts && _onFatalError is { } onFatal)
                {
                    _log.LogWarning("Persistent connect failures — escalating to fatal");
                    _shouldReconnect = false;
                    CancelAllTimers();
                    var msg = $"Persistent connect failures ({_consecutiveConnectFailures})";
                    _readyTcs?.TrySetException(new TransportException(msg));
                    _readyTcs = null;
                    onFatal(msg);
                    return;
                }

                ScheduleReconnect();
            });
        });
    }

    private void CancelConnectTimeout()
    {
        try { _connectTimeoutCts?.Cancel(); } catch { /* swallow */ }
        _connectTimeoutCts = null;
    }

    private void CancelAllTimers()
    {
        CancelConnectTimeout();
        try { _reconnectCts?.Cancel(); } catch { /* swallow */ }
        _reconnectCts = null;
    }

    private void ScheduleReconnect()
    {
        if (!_shouldReconnect) return;
        try { _reconnectCts?.Cancel(); } catch { /* swallow */ }

        _reconnectAttempt++;
        _currentBrokerIndex = _reconnectAttempt % _brokers.Count;

        int cycleIndex = Math.Min(_reconnectAttempt / _brokers.Count, BackoffDelaysSec.Length - 1);
        double delaySec = BackoffDelaysSec[cycleIndex];

        if (delaySec == 0.0)
        {
            _ = Task.Run(async () => await ConnectViaScheduleAsync().ConfigureAwait(false));
            return;
        }

        _log.LogInformation("Reconnect in {Delay}s (attempt={Attempt}, broker={Host})",
            delaySec, _reconnectAttempt, _brokers[_currentBrokerIndex].Host);
        EmitState(new TransportState.Reconnecting(_brokers[_currentBrokerIndex].Host, _reconnectAttempt));

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _reconnectCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(delaySec * 1000), cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            await ConnectViaScheduleAsync().ConfigureAwait(false);
        });
    }

    private Task ConnectViaScheduleAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Schedule(async () =>
        {
            try { await ConnectToCurrentBrokerAsync().ConfigureAwait(false); }
            finally { done.TrySetResult(); }
        });
        return done.Task;
    }

    // ---- Dispatch ----

    private void Dispatch(string topic, byte[] payload)
    {
        // Snapshot under lock — Subscribe / Unsubscribe mutate from the
        // work-queue, but Dispatch is called on MqttConnection's receive
        // thread and may race with an in-flight subscribe.
        //
        // Snapshot semantics: a handler that synchronously calls
        // Unsubscribe continues receiving the rest of THIS dispatch. Same
        // as Kotlin (MqttTransport.kt:285) and Swift (MQTTTransport:419).
        // Acceptable: the only realistic synchronous-Unsubscribe caller
        // would be a one-shot handshake handler, where one duplicated
        // delivery is harmless.
        KeyValuePair<string, Action<string, byte[]>>[] snapshot;
        lock (_subscriptionsLock)
        {
            snapshot = _subscriptions.ToArray();
        }

        foreach (var (pattern, handler) in snapshot)
        {
            if (Topics.Matches(topic, pattern))
            {
                try { handler(topic, payload); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Subscription handler for {Pattern} threw", pattern);
                }
            }
        }
    }

    private void EmitState(TransportState state)
    {
        Channel<TransportState>[] subs;
        lock (_subscribersLock)
        {
            _lastState = state;
            subs = _subscribers.ToArray();
        }
        foreach (var s in subs) s.Writer.TryWrite(state);
    }

    /// <summary>
    /// Reuse <see cref="ConnackPacket"/>'s reason-code lookup table without
    /// allocating a packet record per lookup. Exposed as <c>internal</c>
    /// for tests.
    /// </summary>
    internal static string ConnackReasonString(byte code) =>
        new ConnackPacket(SessionPresent: false, ReasonCode: code, ServerKeepAlive: null).ReasonString;
}
