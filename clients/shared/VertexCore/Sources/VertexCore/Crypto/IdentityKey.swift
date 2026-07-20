import CryptoKit
import Foundation

/// Device identity key (persistent X25519 keypair) for TOFU authentication.
///
/// The identity key is separate from the session DH key. It proves device
/// ownership via HMAC: HMAC-SHA256(ECDH(identity_priv, exit_pub), "vtx-identity-v1" + name).
public struct IdentityKey: Sendable {
    public let privateKey: Curve25519.KeyAgreement.PrivateKey

    public var publicKey: Curve25519.KeyAgreement.PublicKey {
        privateKey.publicKey
    }

    public var publicKeyHex: String {
        privateKey.publicKey.rawRepresentation.hexString
    }

    public init(privateKey: Curve25519.KeyAgreement.PrivateKey) {
        self.privateKey = privateKey
    }

    /// Generate a new random identity key.
    public init() {
        self.privateKey = Curve25519.KeyAgreement.PrivateKey()
    }

    /// Create from raw bytes (32 bytes).
    public init(rawRepresentation: Data) throws {
        self.privateKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: rawRepresentation)
    }

    /// Raw bytes for storage (32 bytes).
    public var rawRepresentation: Data {
        privateKey.rawRepresentation
    }

    /// Compute identity proof for the join handshake.
    ///
    /// proof = HMAC-SHA256(ECDH(identity_priv, exit_pub), "vtx-identity-v1" + name)
    public func proof(exitPublicKey: Curve25519.KeyAgreement.PublicKey, name: String) throws -> Data {
        let sharedSecret = try privateKey.sharedSecretFromKeyAgreement(with: exitPublicKey)
        let sharedData = sharedSecret.withUnsafeBytes { Data($0) }

        let key = SymmetricKey(data: sharedData)
        let message = Data("vtx-identity-v1".utf8) + Data(name.utf8)
        let mac = HMAC<SHA256>.authenticationCode(for: message, using: key)

        return Data(mac)
    }
}

extension Data {
    public var hexString: String {
        map { String(format: "%02x", $0) }.joined()
    }

    public init?(hexString: String) {
        let len = hexString.count / 2
        var data = Data(capacity: len)
        var index = hexString.startIndex
        for _ in 0..<len {
            let nextIndex = hexString.index(index, offsetBy: 2)
            guard let byte = UInt8(hexString[index..<nextIndex], radix: 16) else {
                return nil
            }
            data.append(byte)
            index = nextIndex
        }
        self = data
    }
}
