import Foundation

/// Join handshake message sent by client to exit via control topic.
///
/// Published to: `vpn/{exit}/control/join`
/// Field names and encoding match Go implementation (base64 for keys).
public struct JoinMessage: Codable, Sendable {
    /// Client name (e.g. "macbook")
    public let name: String

    /// Ephemeral X25519 DH public key (base64)
    public let dh: String

    /// Device identity public key (base64), optional for backward compat
    public let id: String?

    /// HMAC proof of identity key ownership (base64)
    public let idSig: String?

    public init(
        name: String,
        dh: String,
        id: String? = nil,
        idSig: String? = nil
    ) {
        self.name = name
        self.dh = dh
        self.id = id
        self.idSig = idSig
    }

    enum CodingKeys: String, CodingKey {
        case name
        case dh
        case id
        case idSig = "id_sig"
    }
}
