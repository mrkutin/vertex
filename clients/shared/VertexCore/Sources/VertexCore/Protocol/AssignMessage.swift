import Foundation

/// IP assignment response from exit after join handshake.
///
/// Received on: `vpn/{exit}/{name}/control`
/// Field names match Go implementation.
public struct AssignMessage: Codable, Sendable {
    /// Assigned TUN IP address (e.g. "10.9.0.5")
    public let ip: String

    /// Subnet mask (e.g. "255.255.255.0")
    public let mask: String?

    /// Gateway/exit TUN IP (e.g. "10.9.0.1")
    public let gw: String

    /// Exit's X25519 DH public key (base64) for session key derivation
    public let dh: String?

    public init(ip: String, mask: String? = nil, gw: String, dh: String? = nil) {
        self.ip = ip
        self.mask = mask
        self.gw = gw
        self.dh = dh
    }
}
