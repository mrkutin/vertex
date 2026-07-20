package ru.vertices.android.core.crypto

import org.bouncycastle.crypto.agreement.X25519Agreement
import org.bouncycastle.crypto.params.X25519PrivateKeyParameters
import org.bouncycastle.crypto.params.X25519PublicKeyParameters
import java.security.SecureRandom

/**
 * X25519 key exchange, byte-exact compatible with Go `crypto/ecdh.X25519` and
 * Swift `Curve25519.KeyAgreement`. Raw 32-byte representation everywhere — no
 * DER/PKCS encoding involved.
 *
 * We go through BouncyCastle's lightweight API so behaviour is uniform across
 * minSdk 26-35 (the platform `KeyPairGenerator("XDH")` with `NamedParameterSpec`
 * is API 33+ only and would force us to maintain a separate code path).
 */
object X25519 {

    init { BouncyCastleProviderInit.ensureInstalled() }

    /** Generate a fresh ephemeral keypair. */
    fun generate(random: SecureRandom = SecureRandom()): KeyPair {
        val priv = X25519PrivateKeyParameters(random)
        val pubBytes = ByteArray(X25519PublicKeyParameters.KEY_SIZE)
        priv.generatePublicKey().encode(pubBytes, 0)
        val privBytes = ByteArray(X25519PrivateKeyParameters.KEY_SIZE)
        priv.encode(privBytes, 0)
        return KeyPair(privateKey = privBytes, publicKey = pubBytes)
    }

    /** Load a private key from raw 32 bytes. Public key is derived. */
    fun fromPrivateBytes(privateKey: ByteArray): KeyPair {
        require(privateKey.size == X25519PrivateKeyParameters.KEY_SIZE) {
            "X25519 private key must be ${X25519PrivateKeyParameters.KEY_SIZE} bytes, got ${privateKey.size}"
        }
        val priv = X25519PrivateKeyParameters(privateKey, 0)
        val pubBytes = ByteArray(X25519PublicKeyParameters.KEY_SIZE)
        priv.generatePublicKey().encode(pubBytes, 0)
        return KeyPair(privateKey = privateKey.copyOf(), publicKey = pubBytes)
    }

    /**
     * X25519 ECDH. Matches Go `priv.ECDH(theirPub)` and CryptoKit
     * `priv.sharedSecretFromKeyAgreement(with:)` byte-for-byte (32-byte raw shared).
     */
    fun ecdh(privateKey: ByteArray, peerPublicKey: ByteArray): ByteArray {
        require(privateKey.size == X25519PrivateKeyParameters.KEY_SIZE) {
            "private key must be 32 bytes"
        }
        require(peerPublicKey.size == X25519PublicKeyParameters.KEY_SIZE) {
            "peer public key must be 32 bytes"
        }
        val priv = X25519PrivateKeyParameters(privateKey, 0)
        val pub = X25519PublicKeyParameters(peerPublicKey, 0)
        val agreement = X25519Agreement().apply { init(priv) }
        val shared = ByteArray(agreement.agreementSize)
        agreement.calculateAgreement(pub, shared, 0)
        return shared
    }

    /** Raw 32-byte X25519 keypair. */
    data class KeyPair(val privateKey: ByteArray, val publicKey: ByteArray) {
        override fun equals(other: Any?): Boolean {
            if (this === other) return true
            if (other !is KeyPair) return false
            return privateKey.contentEquals(other.privateKey) &&
                publicKey.contentEquals(other.publicKey)
        }
        override fun hashCode(): Int =
            privateKey.contentHashCode() * 31 + publicKey.contentHashCode()
    }
}
