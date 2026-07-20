import CryptoKit
import Foundation

/// E2E encryption using X25519 DH key exchange + ChaCha20-Poly1305.
///
/// Wire format: [12B random nonce][encrypted payload][16B auth tag]
/// Total overhead: 28 bytes per packet.
public final class SessionCrypto: Sendable {
    private let symmetricKey: SymmetricKey

    /// Create from a pre-derived symmetric key (after DH + HKDF).
    public init(symmetricKey: SymmetricKey) {
        self.symmetricKey = symmetricKey
    }

    /// Derive a session from X25519 DH shared secret.
    ///
    /// - Parameters:
    ///   - privateKey: Our ephemeral X25519 private key
    ///   - peerPublicKey: Exit's X25519 public key
    /// - Returns: SessionCrypto ready for seal/open
    public static func fromDH(
        privateKey: Curve25519.KeyAgreement.PrivateKey,
        peerPublicKey: Curve25519.KeyAgreement.PublicKey
    ) throws -> SessionCrypto {
        let sharedSecret = try privateKey.sharedSecretFromKeyAgreement(with: peerPublicKey)

        // Salt = clientPub || exitPub (matches Go implementation)
        let salt = privateKey.publicKey.rawRepresentation + peerPublicKey.rawRepresentation

        let derivedKey = sharedSecret.hkdfDerivedSymmetricKey(
            using: SHA256.self,
            salt: salt,
            sharedInfo: Data("broker-tunnel-v1".utf8),
            outputByteCount: 32
        )

        return SessionCrypto(symmetricKey: derivedKey)
    }

    /// Encrypt a packet. Returns [12B nonce][ciphertext][16B tag].
    public func seal(_ plaintext: Data) throws -> Data {
        let nonce = ChaChaPoly.Nonce()
        let sealed = try ChaChaPoly.seal(plaintext, using: symmetricKey, nonce: nonce)
        // Combined = nonce + ciphertext + tag
        return sealed.combined
    }

    /// Decrypt a packet from wire format [12B nonce][ciphertext][16B tag].
    public func open(_ combined: Data) throws -> Data {
        let box = try ChaChaPoly.SealedBox(combined: combined)
        return try ChaChaPoly.open(box, using: symmetricKey)
    }
}
