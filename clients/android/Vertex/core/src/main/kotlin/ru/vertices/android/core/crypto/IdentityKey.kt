package ru.vertices.android.core.crypto

import ru.vertices.android.core.util.toHex
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Persistent X25519 keypair for device identity (TOFU). Mirrors WireGuard-style
 * device-bound auth: the public key is registered on first connection and the
 * client proves ownership on every subsequent connect via an HMAC of an
 * ECDH-derived secret.
 *
 * Wire format (parity with Go `pkg/identity` and Swift `VertexCore.IdentityKey`):
 *
 *     proof = HMAC-SHA256(ECDH(identity_priv, exit_pub),  "vtx-identity-v1" + name)
 */
class IdentityKey(val keyPair: X25519.KeyPair) {

    /**
     * Raw 32-byte private key — what gets persisted to the keystore.
     * Defensive copy on every read: the underlying buffer is the one we sign
     * with, so a caller mutating the returned array would silently corrupt
     * subsequent proofs. Cheap (32 bytes) and matches CryptoKit's value-type
     * semantics on iOS where `rawRepresentation` always materializes a Data.
     */
    val privateKeyBytes: ByteArray get() = keyPair.privateKey.copyOf()

    /** Raw 32-byte public key — what the exit registers in TOFU. */
    val publicKeyBytes: ByteArray get() = keyPair.publicKey.copyOf()

    /** Lowercase hex of the public key. */
    val publicKeyHex: String get() = publicKeyBytes.toHex()

    /**
     * Compute the identity proof for the join handshake.
     *
     * @param exitPublicKey the exit's static X25519 public key (raw 32 bytes).
     * @param name our client name as it appears on the broker (e.g. "android-pixel").
     */
    fun proof(exitPublicKey: ByteArray, name: String): ByteArray {
        val shared = X25519.ecdh(privateKeyBytes, exitPublicKey)
        try {
            val msg = LABEL_BYTES + name.toByteArray(Charsets.UTF_8)
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(shared, "HmacSHA256"))
            return mac.doFinal(msg)
        } finally {
            // Best-effort wipe of the local shared secret. The JVM may keep
            // additional copies in the garbage-collected heap (SecretKeySpec
            // holds its own internal copy that we can't reach), so this is a
            // defence-in-depth measure rather than a guarantee — but it
            // shrinks the window where the raw 32-byte secret sits in our
            // own stack frame waiting for a memory dump to leak.
            shared.fill(0)
        }
    }

    companion object {
        /** Wire-protocol identity HMAC label. NEVER rename — see MIGRATION.md. */
        const val LABEL: String = "vtx-identity-v1"
        private val LABEL_BYTES: ByteArray = LABEL.toByteArray(Charsets.US_ASCII)

        /** Generate a fresh identity (used on first run). */
        fun generate(): IdentityKey = IdentityKey(X25519.generate())

        /** Reload from persisted 32-byte private key. */
        fun fromPrivateBytes(privateKey: ByteArray): IdentityKey =
            IdentityKey(X25519.fromPrivateBytes(privateKey))
    }
}
