import Foundation

/// `BrokerProbe.reorderByRTT` measures TCP-connect time to every URL
/// in the input list in parallel and returns them sorted ascending by
/// RTT. Failed probes (timeout, refusal, no path) keep their original
/// relative position at the tail so a degraded broker still gets tried
/// as a last-resort fallback rather than silently dropped.
///
/// Mirrors `pkg/probe.ReorderByRTT` (Go); semantics MUST stay byte-
/// equivalent — see `pkg/probe/tcp.go` for the canonical implementation.
/// Probe timing excludes TLS / WebSocket handshake — only the TCP
/// round-trip is measured, so a slow certificate chain doesn't skew
/// the network-latency reading.
public enum BrokerProbe {
    /// Per-URL probe outcome paired with original index for stable
    /// tie-breaking inside `sort`.
    private struct Probe {
        let idx: Int
        let url: BrokerURL
        let rttMs: Int?
    }

    /// Reorder broker URLs by ascending TCP-connect RTT. Empty or
    /// single-URL input is a no-op. Total wait is bounded by `timeout`
    /// (probes run in parallel; the slowest individual probe sets the
    /// wall-clock floor).
    public static func reorderByRTT(_ brokers: [BrokerURL], timeout: TimeInterval = 1.5) async -> [BrokerURL] {
        guard brokers.count > 1 else { return brokers }
        let probes = await runProbes(brokers, timeout: timeout)
        return sortByRTT(probes).map(\.url)
    }

    /// Reorder + return the per-host RTT map for logging. When two
    /// URLs share a hostname (e.g. `mqtts://host:8883` and
    /// `wss://host:443`), the lower RTT wins for that host.
    ///
    /// Single uniform code path — even single-broker / empty input
    /// goes through `runProbes` so the rttMs map shape is identical
    /// regardless of input size. Single-broker case still useful so
    /// callers can log latency.
    public static func reorderWithRTTs(_ brokers: [BrokerURL], timeout: TimeInterval = 1.5) async -> (sorted: [BrokerURL], rttMs: [String: Int]) {
        if brokers.isEmpty {
            return ([], [:])
        }
        let probes = await runProbes(brokers, timeout: timeout)
        var rtts: [String: Int] = [:]
        for p in probes {
            guard let ms = p.rttMs else { continue }
            // Per-host display map: keep the lower of the two RTTs when
            // the same host appears with multiple schemes (e.g.
            // mqtts://yc:8883 + wss://yc:443). On exact tie the first
            // probe to complete wins — TaskGroup completion order is
            // not deterministic, but this map is diagnostic only (used
            // for log lines) and does NOT affect the returned ordering.
            if let existing = rtts[p.url.host], existing <= ms { continue }
            rtts[p.url.host] = ms
        }
        return (sortByRTT(probes).map(\.url), rtts)
    }

    /// Format the result of `reorderWithRTTs` as a single human-readable
    /// "host=Xms host=Yms" string for log lines. Failed probes show as
    /// `host=fail`.
    public static func formatOrder(_ sorted: [BrokerURL], rttMs: [String: Int]) -> String {
        sorted.map { url in
            if let ms = rttMs[url.host] {
                return "\(url.host)=\(ms)ms"
            }
            return "\(url.host)=fail"
        }
        .joined(separator: " ")
    }

    // MARK: - Private

    private static func runProbes(_ brokers: [BrokerURL], timeout: TimeInterval) async -> [Probe] {
        await withTaskGroup(of: Probe.self) { group in
            for (i, b) in brokers.enumerated() {
                group.addTask {
                    let r = await TCPRTT.measure(host: b.host, port: UInt16(b.port), timeout: timeout)
                    let ms: Int? = if case .success(let v) = r { v } else { nil }
                    return Probe(idx: i, url: b, rttMs: ms)
                }
            }
            var out: [Probe] = []
            out.reserveCapacity(brokers.count)
            for await p in group {
                out.append(p)
            }
            return out
        }
    }

    /// Sort: successful probes ascending by RTT (with original index
    /// as tiebreaker for stability), then failed probes in their
    /// original order. Single source of truth.
    private static func sortByRTT(_ probes: [Probe]) -> [Probe] {
        probes.sorted { a, b in
            switch (a.rttMs, b.rttMs) {
            case (let x?, let y?):
                if x != y { return x < y }
                return a.idx < b.idx
            case (.some, .none):
                return true
            case (.none, .some):
                return false
            case (.none, .none):
                return a.idx < b.idx
            }
        }
    }
}
