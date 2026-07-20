import Foundation
import os

/// Snapshot of one exit's most recent discovery heartbeat plus the wall-clock
/// time the tracker observed it. Mirrors `pkg/discovery.ExitInfo` (Go).
public struct ExitInfo: Sendable, Hashable {
    public let id: String
    public let country: String?
    public let clients: Int
    public let maxClients: Int
    public let brokerRTTms: [String: Int]
    public let dhPubkey: String?
    public let receivedAt: Date

    public init(
        id: String,
        country: String?,
        clients: Int,
        maxClients: Int,
        brokerRTTms: [String: Int],
        dhPubkey: String?,
        receivedAt: Date
    ) {
        self.id = id
        self.country = country
        self.clients = clients
        self.maxClients = maxClients
        self.brokerRTTms = brokerRTTms
        self.dhPubkey = dhPubkey
        self.receivedAt = receivedAt
    }
}

/// Accumulates exit-node heartbeats and runs the same scoring/selection logic
/// as `pkg/discovery` in Go. Lower score = better.
///
/// Formula: `score = brokerRTTms * (1 + clients / capacity * loadFactor)`,
/// where capacity falls back to 253 (a /24 IP pool minus reserved hosts) and
/// loadFactor = 2.0 by default. RTT defaults to 100 ms when missing so an
/// exit without a measurement is still pickable but loses to any exit with a
/// real number.
///
/// `shouldSwitch` carries a 1.5x flap-guard: only recommend switching when
/// `bestScore * 1.5 < currentScore`.
///
/// No equivalent of Go's `Pump` — Swift callers feed `handle(_:)` directly
/// from the MQTT subscribe closure (see `PacketTunnelProvider`); there is no
/// `liveness.Feed` channel adapter in the Swift surface.
public final class DiscoveryTracker: Sendable {
    private let state: OSAllocatedUnfairLock<[String: ExitInfo]>
    private let loadFactor: Double
    private let staleAge: TimeInterval

    /// /24 minus reserved hosts (.0, .1 gw, .255 broadcast, .2-.254 client pool).
    private static let defaultCapacity = 253
    /// Used when no broker-RTT entry exists for the requested host.
    private static let defaultRTTms = 100

    public init(loadFactor: Double = 2.0, staleAge: TimeInterval = 90) {
        self.loadFactor = loadFactor
        self.staleAge = staleAge
        self.state = OSAllocatedUnfairLock(initialState: [:])
    }

    /// Ingest one decoded heartbeat. Idempotent — the latest heartbeat for an
    /// exit ID replaces the previous one.
    public func handle(_ hb: DiscoveryHeartbeat, receivedAt: Date = Date()) {
        let info = ExitInfo(
            id: hb.id,
            country: hb.country,
            clients: hb.clients ?? 0,
            maxClients: hb.maxClients ?? 0,
            brokerRTTms: hb.brokerRTTms ?? [:],
            dhPubkey: hb.dhPubkey,
            receivedAt: receivedAt
        )
        state.withLock { exits in
            exits[info.id] = info
        }
    }

    /// Drop the entry for `exitID`. Reserved for the day we wire LWT-style
    /// removal events; auto-resolve relies on staleAge today.
    public func remove(exitID: String) {
        _ = state.withLock { exits in
            exits.removeValue(forKey: exitID)
        }
    }

    /// Best non-stale, non-full exit for the given broker host. Nil when the
    /// tracker hasn't seen a usable heartbeat yet.
    public func bestExit(forBroker brokerHost: String) -> String? {
        state.withLock { exits in
            bestExit(in: exits, brokerHost: brokerHost, excluding: nil)
        }
    }

    /// 1.5x-tolerance switch decision. Returns the target exit when an
    /// alternative is significantly better than `currentExit`. When
    /// `currentExit` is missing or stale, returns the best alternative
    /// regardless of margin.
    public func shouldSwitch(currentExit: String, brokerHost: String) -> String? {
        state.withLock { exits in
            guard let current = exits[currentExit], !isStale(current) else {
                return bestExit(in: exits, brokerHost: brokerHost, excluding: currentExit)
            }
            let currentScore = score(current, brokerHost: brokerHost)

            var bestID: String?
            var bestScore = Double.greatestFiniteMagnitude
            for (_, info) in exits {
                if info.id == currentExit || isStale(info) { continue }
                if info.maxClients > 0 && info.clients >= info.maxClients { continue }
                let s = score(info, brokerHost: brokerHost)
                if s < bestScore {
                    bestScore = s
                    bestID = info.id
                }
            }
            if let bestID, bestScore * 1.5 < currentScore {
                return bestID
            }
            return nil
        }
    }

    /// Most recent non-stale heartbeat for the given exit ID, or nil. Used
    /// by the join handshake to pull `dhPubkey` for the identity proof
    /// without re-waiting on the discovery stream.
    public func info(for exitID: String) -> ExitInfo? {
        state.withLock { exits in
            guard let info = exits[exitID], !isStale(info) else { return nil }
            return info
        }
    }

    /// True if the exit has a recent (non-stale) heartbeat.
    public func isAvailable(_ exitID: String) -> Bool {
        state.withLock { exits in
            guard let info = exits[exitID] else { return false }
            return !isStale(info)
        }
    }

    /// Snapshot of all known exits. `includeStale: true` returns even
    /// expired entries (used by the auto-resolve fallback chain when no
    /// fresh heartbeat is in yet).
    public func snapshot(includeStale: Bool = false) -> [ExitInfo] {
        state.withLock { exits in
            exits.values.filter { includeStale || !isStale($0) }
        }
    }

    // MARK: - Private helpers (must run under the lock)

    private func bestExit(in exits: [String: ExitInfo], brokerHost: String, excluding: String?) -> String? {
        var bestID: String?
        var bestScore = Double.greatestFiniteMagnitude
        for (_, info) in exits {
            if info.id == excluding || isStale(info) { continue }
            if info.maxClients > 0 && info.clients >= info.maxClients { continue }
            let s = score(info, brokerHost: brokerHost)
            if s < bestScore {
                bestScore = s
                bestID = info.id
            }
        }
        return bestID
    }

    private func score(_ info: ExitInfo, brokerHost: String) -> Double {
        let rtt = brokerRTT(info: info, brokerHost: brokerHost)
        let effectiveRTT = rtt > 0 ? Double(rtt) : Double(Self.defaultRTTms)
        let capacity = info.maxClients > 0 ? Double(info.maxClients) : Double(Self.defaultCapacity)
        return effectiveRTT * (1.0 + Double(info.clients) / capacity * loadFactor)
    }

    private func brokerRTT(info: ExitInfo, brokerHost: String) -> Int {
        if let exact = info.brokerRTTms[brokerHost] {
            return exact
        }
        let bare = Self.stripPort(brokerHost)
        for (host, rtt) in info.brokerRTTms where Self.stripPort(host) == bare {
            return rtt
        }
        return 0
    }

    private static func stripPort(_ host: String) -> String {
        guard let colon = host.lastIndex(of: ":") else { return host }
        return String(host[..<colon])
    }

    private func isStale(_ info: ExitInfo) -> Bool {
        Date().timeIntervalSince(info.receivedAt) > staleAge
    }
}
