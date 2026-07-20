using Vertex.Core.Protocol;

namespace Vertex.Core.Discovery;

/// <summary>
/// Accumulates exit-node heartbeats and runs the same scoring / selection
/// logic as Go <c>pkg/discovery</c>, Swift <c>DiscoveryTracker</c>, and
/// Kotlin <c>DiscoveryTracker</c>. Lower score = better.
///
/// Formula: <c>score = brokerRTTms * (1 + clients / capacity * loadFactor)</c>,
/// where capacity falls back to 253 (a /24 IP pool minus reserved hosts)
/// and loadFactor = 2.0 by default. RTT defaults to 100 ms when missing,
/// so an exit without a measurement is still pickable but loses to any
/// exit with a real number.
///
/// <see cref="ShouldSwitch"/> carries a 1.5× flap-guard: only recommend
/// switching when <c>bestScore * 1.5 &lt; currentScore</c>.
/// </summary>
public sealed class DiscoveryTracker
{
    /// <summary>
    /// Canonical scoring constants. MUST stay in sync with the Go reference
    /// in <c>pkg/discovery/discovery.go</c> (defaultCapacity, NewTracker
    /// loadFactor + staleAge, ShouldSwitch threshold). Drift here = drift
    /// from production exits.
    /// </summary>
    private const int    DefaultCapacity   = 253;   // /24 minus reserved hosts (.0, .1 gw, .255 broadcast)
    private const int    DefaultRttMs      = 100;   // when no broker-RTT entry exists
    private const double DefaultLoadFactor = 2.0;
    private const double FlapGuardRatio    = 1.5;   // ShouldSwitch only when bestScore * 1.5 < currentScore
    private static readonly TimeSpan DefaultStaleAge = TimeSpan.FromSeconds(90);

    private readonly object _lock = new();
    private readonly Dictionary<string, ExitInfo> _state = new();
    private readonly double _loadFactor;
    private readonly TimeSpan _staleAge;
    private readonly Func<DateTime> _clock;

    public DiscoveryTracker(
        double loadFactor = DefaultLoadFactor,
        TimeSpan? staleAge = null,
        Func<DateTime>? clock = null)
    {
        _loadFactor = loadFactor;
        _staleAge   = staleAge ?? DefaultStaleAge;
        _clock      = clock ?? (() => DateTime.UtcNow);
    }

    private DateTime Now() => _clock();

    /// <summary>
    /// Ingest one decoded heartbeat. Idempotent — the latest heartbeat for
    /// an exit ID replaces the previous one.
    /// </summary>
    public void Handle(DiscoveryHeartbeat hb, DateTime? receivedAt = null)
    {
        var info = new ExitInfo(
            Id:           hb.Id,
            Country:      hb.Country,
            Clients:      hb.Clients ?? 0,
            MaxClients:   hb.MaxClients ?? 0,
            BrokerRttMs:  (IReadOnlyDictionary<string, int>?)hb.BrokerRttMs ?? new Dictionary<string, int>(),
            DhPubkey:     hb.DhPubkey,
            ReceivedAt:   receivedAt ?? Now());

        lock (_lock) { _state[info.Id] = info; }
    }

    /// <summary>Drop the entry for <paramref name="exitId"/>. Reserved for the day we wire LWT-style removal events.</summary>
    public void Remove(string exitId)
    {
        lock (_lock) { _state.Remove(exitId); }
    }

    /// <summary>
    /// Best non-stale, non-full exit for the given broker host, or
    /// <c>null</c> when the tracker hasn't seen a usable heartbeat yet.
    /// </summary>
    public string? BestExit(string brokerHost)
    {
        lock (_lock) { return BestExitLocked(brokerHost, excluding: null); }
    }

    /// <summary>
    /// 1.5×-tolerance switch decision. Returns the target exit when an
    /// alternative is significantly better than <paramref name="currentExit"/>.
    /// When <paramref name="currentExit"/> is missing or stale, returns the
    /// best alternative regardless of margin.
    /// </summary>
    public string? ShouldSwitch(string currentExit, string brokerHost)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(currentExit, out var current) || IsStaleLocked(current))
            {
                return BestExitLocked(brokerHost, excluding: currentExit);
            }
            double currentScore = ScoreLocked(current, brokerHost);

            string? bestId = null;
            double bestScore = double.MaxValue;
            foreach (var info in _state.Values)
            {
                if (info.Id == currentExit) continue;
                if (IsStaleLocked(info)) continue;
                if (info.MaxClients > 0 && info.Clients >= info.MaxClients) continue;
                double s = ScoreLocked(info, brokerHost);
                if (s < bestScore)
                {
                    bestScore = s;
                    bestId = info.Id;
                }
            }
            return bestId is not null && bestScore * FlapGuardRatio < currentScore ? bestId : null;
        }
    }

    /// <summary>
    /// Most recent non-stale heartbeat for the given exit ID, or
    /// <c>null</c>. Used by the join handshake to pull <c>dhPubkey</c>
    /// for the identity proof without re-waiting on the discovery stream.
    /// </summary>
    public ExitInfo? Info(string exitId)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(exitId, out var info)) return null;
            return IsStaleLocked(info) ? null : info;
        }
    }

    /// <summary>True if the exit has a recent (non-stale) heartbeat.</summary>
    public bool IsAvailable(string exitId)
    {
        lock (_lock)
        {
            return _state.TryGetValue(exitId, out var info) && !IsStaleLocked(info);
        }
    }

    /// <summary>
    /// Snapshot of all known exits. <paramref name="includeStale"/> = true
    /// returns even expired entries (used by the auto-resolve fallback
    /// chain when no fresh heartbeat is in yet).
    /// </summary>
    public IReadOnlyList<ExitInfo> Snapshot(bool includeStale = false)
    {
        lock (_lock)
        {
            return includeStale
                ? _state.Values.ToList()
                : _state.Values.Where(i => !IsStaleLocked(i)).ToList();
        }
    }

    // ---- private helpers (must run under _lock) ----

    private string? BestExitLocked(string brokerHost, string? excluding)
    {
        string? bestId = null;
        double bestScore = double.MaxValue;
        foreach (var info in _state.Values)
        {
            if (info.Id == excluding) continue;
            if (IsStaleLocked(info)) continue;
            if (info.MaxClients > 0 && info.Clients >= info.MaxClients) continue;
            double s = ScoreLocked(info, brokerHost);
            if (s < bestScore)
            {
                bestScore = s;
                bestId = info.Id;
            }
        }
        return bestId;
    }

    private double ScoreLocked(ExitInfo info, string brokerHost)
    {
        int rtt = BrokerRttFor(info, brokerHost);
        double effectiveRtt = rtt > 0 ? rtt : DefaultRttMs;
        double capacity = info.MaxClients > 0 ? info.MaxClients : DefaultCapacity;
        return effectiveRtt * (1.0 + info.Clients / capacity * _loadFactor);
    }

    private static int BrokerRttFor(ExitInfo info, string brokerHost)
    {
        if (info.BrokerRttMs.TryGetValue(brokerHost, out var exact))
        {
            return exact;
        }
        // Strip ":port" suffix and try again — production heartbeats key
        // by bare host. Same rule as Go pkg/discovery.stripPort: NOT
        // IPv6-literal-safe (raw "::1" would strip a colon), but exits
        // never emit IPv6-literal hostnames, so the IPv6 path is dead
        // code today.
        string bare = StripPort(brokerHost);
        foreach (var (host, rtt) in info.BrokerRttMs)
        {
            if (StripPort(host) == bare) return rtt;
        }
        return 0;
    }

    /// <summary>
    /// Strip a trailing <c>:port</c> from a host. Mirrors Go
    /// <c>pkg/discovery.stripPort</c>: <c>colon &gt; 0</c> guard so a
    /// degenerate <c>":8883"</c> doesn't silently collapse to an empty
    /// bare-host that could collide in the lookup map.
    /// </summary>
    internal static string StripPort(string host)
    {
        int colon = host.LastIndexOf(':');
        return colon > 0 ? host[..colon] : host;
    }

    private bool IsStaleLocked(ExitInfo info) => Now() - info.ReceivedAt > _staleAge;
}
