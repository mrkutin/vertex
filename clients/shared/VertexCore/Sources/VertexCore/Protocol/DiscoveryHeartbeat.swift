import Foundation

/// Exit discovery heartbeat published as retained on `discovery/exits/{id}`.
/// Field names match Go implementation exactly.
public struct DiscoveryHeartbeat: Codable, Sendable {
    /// Exit identifier (e.g. "aws", "sto")
    public let id: String

    /// Country code (e.g. "CA", "SE")
    public let country: String?

    /// Number of connected clients
    public let clients: Int?

    /// Maximum client capacity
    public let maxClients: Int?

    /// Broker RTT measurements: hostname → ms
    public let brokerRTTms: [String: Int]?

    /// Uptime in seconds
    public let uptime: Int64?

    /// Unix timestamp
    public let ts: Int64?

    /// Exit's static DH public key (base64)
    public let dhPubkey: String?

    enum CodingKeys: String, CodingKey {
        case id
        case country
        case clients
        case maxClients = "max_clients"
        case brokerRTTms = "broker_rtt_ms"
        case uptime
        case ts
        case dhPubkey = "dh_pubkey"
    }
}
