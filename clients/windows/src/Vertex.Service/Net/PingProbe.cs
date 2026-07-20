using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Util;

namespace Vertex.Service.Net;

/// <summary>
/// Periodic end-to-end RTT probe over the active tunnel. Connects via TCP
/// to a public anchor (Cloudflare 1.1.1.1:443 by default) once per
/// <see cref="ProbeInterval"/> and exposes the result as
/// <see cref="Current"/>.
///
/// <para><b>Sticky semantics</b> (locked-in by the IPC contract on
/// <c>ConnectionStatus.PingMs</c>): a probe failure (timeout, refused,
/// path blip) does NOT clear <see cref="Current"/>; only an explicit
/// <see cref="Reset"/> does. The intent is that the SpeedPill UI shows a
/// stable last-known reading rather than flicker between "known" and
/// "unknown" on a one-off network hiccup. The TunnelEngine calls
/// <see cref="Reset"/> on transitions to <c>Disconnected</c> and starts a
/// new <see cref="RunAsync"/> loop on transitions to <c>Connected</c>.</para>
///
/// <para>The probe is fire-and-forget at the call site: <see cref="RunAsync"/>
/// loops until cancellation, raising <see cref="PingMsChanged"/> only when
/// the value actually changes (debounce). Producers that push
/// <c>ConnectionStatus</c> over IPC consult <see cref="Current"/> on every
/// status update; the change event lets them push a status frame
/// immediately after a fresh measurement instead of waiting for the next
/// poll tick.</para>
/// </summary>
public sealed class PingProbe
{
    /// <summary>Cloudflare anycast — closest reachable from any path. Mirrors macOS <c>TunnelViewModel</c>.</summary>
    public const string DefaultHost = "1.1.1.1";
    public const int    DefaultPort = 443;

    /// <summary>One probe per minute. Match macOS — long enough to avoid load, short enough that handoff (Wi-Fi → Cellular) refreshes within ~1 min.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(1);

    /// <summary>Per-probe timeout. RTT values >2.5s saturate the SpeedPill ping bucket and are indistinguishable from "unreachable" anyway.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(2500);

    private readonly string _host;
    private readonly int    _port;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly Func<string, int, TimeSpan, CancellationToken, Task<int?>> _probe;
    private readonly ILogger<PingProbe> _log;

    private readonly object _gate = new();
    private int? _current;

    public PingProbe(
        string? host = null,
        int? port = null,
        TimeSpan? interval = null,
        TimeSpan? timeout = null,
        Func<string, int, TimeSpan, CancellationToken, Task<int?>>? probe = null,
        ILogger<PingProbe>? log = null)
    {
        _host = host ?? DefaultHost;
        _port = port ?? DefaultPort;
        _interval = interval ?? DefaultInterval;
        _timeout = timeout ?? DefaultTimeout;
        _probe = probe ?? TcpRtt.MeasureAsync;
        _log = log ?? NullLogger<PingProbe>.Instance;
    }

    public TimeSpan ProbeInterval => _interval;

    /// <summary>Last successful RTT in ms, or null if never measured / explicitly reset. Sticky across transient failures.</summary>
    public int? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Raised when <see cref="Current"/> transitions (success →
    /// success-with-different-value, or reset → null). Not raised on
    /// transient probe failure.
    /// <para>
    /// The handler is invoked outside the internal lock so a slow
    /// listener cannot stall the probe loop. As a consequence, if a
    /// caller invokes <see cref="ProbeOnceAsync"/> concurrently with
    /// <see cref="RunAsync"/> (production does not — see contract on
    /// <see cref="RunAsync"/>), the value passed to the handler may
    /// differ from <see cref="Current"/> by the time the handler reads
    /// it. TunnelEngine never overlaps probes, so this is informational.
    /// </para>
    /// </summary>
    public event Action<int?>? PingMsChanged;

    /// <summary>
    /// Run the probe loop until <paramref name="ct"/> is cancelled.
    /// First measurement happens immediately; subsequent ones wait
    /// <see cref="ProbeInterval"/>. Idempotent on cancellation —
    /// returns cleanly on a cancelled token.
    /// <para>
    /// TunnelEngine starts this loop on the Connected transition and
    /// is expected to <c>cts.Cancel()</c> + <c>await</c> the returned
    /// task before calling <see cref="Reset"/> on the Disconnected
    /// transition; that ordering eliminates the only race window where
    /// an in-flight probe could re-set <see cref="Current"/> after
    /// <see cref="Reset"/> nulled it.
    /// </para>
    /// <para>
    /// Note on first-probe timing: macOS sleeps 2s before its first
    /// probe because the iOS/macOS timer fires synchronously with the
    /// app session, before the tunnel handshake completes. On Windows
    /// this loop is started AFTER the Connected transition (handshake
    /// already complete), so no warm-up delay is needed.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await ProbeOnceAsync(ct).ConfigureAwait(false);
                try
                {
                    await Task.Delay(_interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    /// <summary>
    /// Run a single probe, update <see cref="Current"/> on success, and
    /// raise <see cref="PingMsChanged"/> if the value changed. Exposed
    /// for tests; production code uses <see cref="RunAsync"/>.
    /// </summary>
    public async Task ProbeOnceAsync(CancellationToken ct)
    {
        int? rtt;
        try
        {
            rtt = await _probe(_host, _port, _timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ping probe to {Host}:{Port} threw", _host, _port);
            rtt = null;
        }

        if (rtt is null)
        {
            // Sticky: do NOT mutate _current on transient failure.
            _log.LogDebug("Ping probe to {Host}:{Port} failed; keeping last value {Last}", _host, _port, _current);
            return;
        }

        bool changed;
        int? notify;
        lock (_gate)
        {
            changed = _current != rtt;
            _current = rtt;
            notify = _current;
        }
        if (changed) PingMsChanged?.Invoke(notify);
    }

    /// <summary>
    /// Clear the sticky value. Call on transitions to Disconnected; on
    /// the next IPC status push the field will be omitted (null) and
    /// the App must treat that as "unknown" (per the
    /// <c>ConnectionStatus.PingMs</c> contract).
    /// <para>
    /// Caller MUST ensure no <see cref="ProbeOnceAsync"/> is in flight
    /// when <see cref="Reset"/> runs — in production, by cancelling the
    /// <see cref="RunAsync"/> token and awaiting the loop's completion
    /// before calling Reset. Without that ordering a probe that
    /// resolves between Cancel() and Reset() can re-fire
    /// <see cref="PingMsChanged"/> with a stale value, leaving the
    /// listener with a Current that does not match the last delivered
    /// notification.
    /// </para>
    /// </summary>
    public void Reset()
    {
        bool changed;
        lock (_gate)
        {
            changed = _current is not null;
            _current = null;
        }
        if (changed) PingMsChanged?.Invoke(null);
    }
}
