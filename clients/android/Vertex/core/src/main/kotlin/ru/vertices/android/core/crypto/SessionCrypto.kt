package ru.vertices.android.core.crypto

/**
 * Per-session ChaCha20-Poly1305 derived from an X25519 ECDH agreement.
 *
 * Wire format (parity with Swift `VertexCore.SessionCrypto` and Go `pkg/crypto`):
 *
 *     [12B random nonce][ciphertext][16B Poly1305 tag]
 *
 * Total overhead: 28 bytes per packet.
 */
class SessionCrypto private constructor(private val cipher: ChaChaPoly) {

    /** Encrypt one packet. Output length = `plaintext.size + 28`. */
    fun seal(plaintext: ByteArray): ByteArray = cipher.seal(plaintext)

    /** Decrypt one packet from wire format. */
    fun open(combined: ByteArray): ByteArray = cipher.open(combined)

    companion object {
        /** Wire-protocol HKDF info string. NEVER rename — see MIGRATION.md. */
        private val HKDF_INFO: ByteArray = "broker-tunnel-v1".toByteArray(Charsets.US_ASCII)

        /** Length of the derived symmetric key in bytes. */
        private const val DERIVED_KEY_BYTES = 32

        /**
         * Derive a [SessionCrypto] from an X25519 DH exchange.
         *
         * @param myPrivateKey our (client ephemeral) X25519 private key, raw 32 bytes.
         * @param theirPublicKey peer (exit static) X25519 public key, raw 32 bytes.
         * @param clientPublicKey raw 32 bytes — the client side of the salt.
         * @param exitPublicKey   raw 32 bytes — the exit side of the salt.
         *
         * Salt is `clientPublicKey || exitPublicKey` (deterministic ordering — both
         * sides agree on direction without negotiating).
         */
        fun fromDH(
            myPrivateKey: ByteArray,
            theirPublicKey: ByteArray,
            clientPublicKey: ByteArray,
            exitPublicKey: ByteArray,
        ): SessionCrypto {
            require(clientPublicKey.size == 32 && exitPublicKey.size == 32) {
                "client/exit pubkeys must be 32 bytes for HKDF salt"
            }
            val shared = X25519.ecdh(myPrivateKey, theirPublicKey)
            val salt = ByteArray(clientPublicKey.size + exitPublicKey.size).also {
                System.arraycopy(clientPublicKey, 0, it, 0,                       clientPublicKey.size)
                System.arraycopy(exitPublicKey,   0, it, clientPublicKey.size,    exitPublicKey.size)
            }
            val derived = Hkdf.deriveSha256(
                ikm = shared,
                salt = salt,
                info = HKDF_INFO,
                outputBytes = DERIVED_KEY_BYTES,
            )
            return SessionCrypto(ChaChaPoly(derived))
        }

        /** Test/diagnostic constructor — use [fromDH] in production code paths. */
        fun fromKey(key: ByteArray): SessionCrypto = SessionCrypto(ChaChaPoly(key))
    }
}
