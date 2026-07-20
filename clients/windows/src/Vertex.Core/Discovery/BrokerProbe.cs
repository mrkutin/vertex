using Vertex.Core.Config;
using Vertex.Core.Util;

namespace Vertex.Core.Discovery;

/// <summary>
/// Reorders broker URLs by ascending TCP-connect RTT before a long-lived
/// MQTT connection is opened, so the transport's failover list points at
/// the fastest reachable broker first. Failed probes (timeout, refusal,
/// no path) keep their original relative position at the tail — a
/// degraded broker still gets tried as last-resort fallback rather than
/// silently dropped.
///
/// Mirror of Swift <c>BrokerProbe.reorderByRTT</c> and Kotlin
/// <c>BrokerProbe.reorderByRtt</c> — semantics MUST stay byte-equivalent
/// with the Go reference (<c>pkg/probe.ReorderByRTT</c>). Probe timing
/// excludes TLS / WebSocket handshake.
/// </summary>
public static class BrokerProbe
{
    /// <summary>Default total wait. Probes run in parallel so the slowest individual probe sets the wall-clock floor.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly record struct Probe(int Idx, BrokerUrl Url, int? RttMs);

    /// <summary>
    /// Reorder broker URLs by ascending TCP-connect RTT. Empty or
    /// single-URL input is a no-op (no probes issued).
    /// </summary>
    public static async Task<IReadOnlyList<BrokerUrl>> ReorderByRttAsync(
        IReadOnlyList<BrokerUrl> brokers,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (brokers.Count <= 1) return brokers;
        var probes = await RunProbesAsync(brokers, timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
        return SortByRtt(probes).Select(p => p.Url).ToList();
    }

    /// <summary>
    /// Reorder + return the per-host RTT map for logging. When two URLs
    /// share a hostname (e.g. <c>mqtts://h:8883</c> and
    /// <c>wss://h:443</c>), the lower RTT wins for that host.
    /// </summary>
    public static async Task<(IReadOnlyList<BrokerUrl> Sorted, IReadOnlyDictionary<string, int> RttMs)>
        ReorderWithRttsAsync(
            IReadOnlyList<BrokerUrl> brokers,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
    {
        if (brokers.Count == 0) return (Array.Empty<BrokerUrl>(), new Dictionary<string, int>());
        var probes = await RunProbesAsync(brokers, timeout ?? DefaultTimeout, ct).ConfigureAwait(false);

        var rtts = new Dictionary<string, int>(brokers.Count);
        foreach (var p in probes)
        {
            if (p.RttMs is not int ms) continue;
            // Keep the lower of two RTTs when the same host appears with
            // multiple schemes (mqtts:8883 + wss:443). On exact tie, the
            // first probe to finish wins — completion order is not
            // deterministic, but this map is diagnostic only.
            if (rtts.TryGetValue(p.Url.Host, out var existing) && existing <= ms) continue;
            rtts[p.Url.Host] = ms;
        }

        return (SortByRtt(probes).Select(p => p.Url).ToList(), rtts);
    }

    /// <summary>
    /// Format the result of <see cref="ReorderWithRttsAsync"/> as a
    /// single human-readable <c>host=Xms host=Yms</c> string for log
    /// lines. Failed probes show as <c>host=fail</c>.
    /// </summary>
    public static string FormatOrder(
        IReadOnlyList<BrokerUrl> sorted,
        IReadOnlyDictionary<string, int> rttMs)
    {
        return string.Join(' ', sorted.Select(u =>
            rttMs.TryGetValue(u.Host, out var ms) ? $"{u.Host}={ms}ms" : $"{u.Host}=fail"));
    }

    // ---- private ----

    private static async Task<List<Probe>> RunProbesAsync(
        IReadOnlyList<BrokerUrl> brokers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var tasks = new Task<Probe>[brokers.Count];
        for (int i = 0; i < brokers.Count; i++)
        {
            int idx = i;
            var url = brokers[i];
            // Wrap each probe in Task.Run so the synchronous prefix of
            // MeasureAsync (TcpClient ctor + Stopwatch start + ConnectAsync's
            // sync prefix) does NOT serialise on the calling thread.
            // Equivalent to Swift's `addTask` and Kotlin's
            // `async(Dispatchers.IO)`.
            tasks[i] = Task.Run(async () =>
            {
                int? ms = await TcpRtt.MeasureAsync(url.Host, url.Port, timeout, ct).ConfigureAwait(false);
                return new Probe(idx, url, ms);
            }, ct);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToList();
    }

    /// <summary>
    /// Successful probes ascending by RTT (with original index as
    /// tie-breaker for stability), then failed probes in their original
    /// order.
    ///
    /// Note on tie-breaks: native ports (Swift / Kotlin / .NET) all
    /// tie-break equal-RTT pairs by original idx for determinism. The Go
    /// reference (<c>pkg/probe.ReorderByRTT</c>, <c>sort.Slice</c>) does
    /// NOT — equal-RTT pairs come out in arbitrary order on Go.
    /// Practically irrelevant (RTTs in ms rarely tie), but worth knowing.
    /// </summary>
    private static List<Probe> SortByRtt(IEnumerable<Probe> probes)
    {
        return probes.OrderBy(p => p, ProbeComparer.Instance).ToList();
    }

    private sealed class ProbeComparer : IComparer<Probe>
    {
        public static readonly ProbeComparer Instance = new();

        public int Compare(Probe a, Probe b)
        {
            return (a.RttMs, b.RttMs) switch
            {
                (int x, int y) when x != y => x.CompareTo(y),
                (int _, int _)             => a.Idx.CompareTo(b.Idx),
                (int _, null)              => -1,
                (null,  int _)             => 1,
                _                          => a.Idx.CompareTo(b.Idx),
            };
        }
    }
}
