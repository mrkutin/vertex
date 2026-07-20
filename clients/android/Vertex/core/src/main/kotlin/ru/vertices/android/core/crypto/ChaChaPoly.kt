package ru.vertices.android.core.crypto

import org.bouncycastle.crypto.engines.ChaCha7539Engine
import org.bouncycastle.crypto.macs.Poly1305
import org.bouncycastle.crypto.modes.ChaCha20Poly1305
import org.bouncycastle.crypto.params.AEADParameters
import org.bouncycastle.crypto.params.KeyParameter
import org.bouncycastle.crypto.params.ParametersWithIV
import java.security.SecureRandom

/**
 * ChaCha20-Poly1305 AEAD, IETF variant (96-bit nonce, 16-byte tag).
 *
 * Wire format — matches Go `pkg/crypto.Cipher.Seal` and Swift CryptoKit
 * `ChaChaPoly.seal(...).combined`:
 *
 *     [12B nonce][ciphertext][16B Poly1305 tag]
 *
 * Implementation: BouncyCastle's lightweight `ChaCha20Poly1305` engine, called
 * directly. We deliberately avoid `Cipher.getInstance(...)` indirection — the
 * JCE name registered for BouncyCastle's ChaCha20-Poly1305 differs across
 * versions and Android API levels (some register it as `"ChaCha20-Poly1305"`,
 * some require `"CHACHA20-POLY1305/None/NoPadding"`, some don't expose it at
 * all on API 26-27). The lightweight API has none of that — same call across
 * every minSdk we support, and it shaves the entire JCE provider lookup
 * overhead off the per-packet hot path.
 *
 * The unused engine imports anchor the dependency graph for ProGuard so that
 * R8 doesn't strip the BC primitives we rely on.
 */
class ChaChaPoly(private val key: ByteArray) {

    init {
        require(key.size == KEY_SIZE) { "key must be $KEY_SIZE bytes, got ${key.size}" }
        BouncyCastleProviderInit.ensureInstalled()
    }

    /**
     * One ChaCha20Poly1305 engine per thread. The packet plane pins seal()
     * to the TUN-up thread and open() to the MQTT-callback thread, so this
     * collapses the per-packet allocation of the BC engine (≈ a few µs each
     * on the perf path that runs at line-rate on a phone CPU) to a single
     * one-time cost. `init()` is idempotent on this engine and resets all
     * internal state, so reusing the instance is safe across packets.
     */
    private val cipherTL: ThreadLocal<ChaCha20Poly1305> =
        ThreadLocal.withInitial { ChaCha20Poly1305() }

    fun seal(plaintext: ByteArray, random: SecureRandom = SecureRandom()): ByteArray {
        val nonce = ByteArray(NONCE_SIZE).also(random::nextBytes)
        val cipher = cipherTL.get()
        cipher.init(true, AEADParameters(KeyParameter(key), TAG_SIZE * 8, nonce))

        val outBuf = ByteArray(NONCE_SIZE + cipher.getOutputSize(plaintext.size))
        // Lay the nonce down first so the result matches CryptoKit `combined`.
        System.arraycopy(nonce, 0, outBuf, 0, NONCE_SIZE)
        var written = NONCE_SIZE
        written += cipher.processBytes(plaintext, 0, plaintext.size, outBuf, written)
        written += cipher.doFinal(outBuf, written)
        // doFinal may produce fewer bytes than getOutputSize predicted — trim to actual.
        return if (written == outBuf.size) outBuf else outBuf.copyOfRange(0, written)
    }

    fun open(combined: ByteArray): ByteArray {
        require(combined.size >= NONCE_SIZE + TAG_SIZE) {
            "ciphertext too short: ${combined.size} < ${NONCE_SIZE + TAG_SIZE}"
        }
        val nonce = combined.copyOfRange(0, NONCE_SIZE)
        val ctLen = combined.size - NONCE_SIZE
        val cipher = cipherTL.get()
        cipher.init(false, AEADParameters(KeyParameter(key), TAG_SIZE * 8, nonce))

        val outBuf = ByteArray(cipher.getOutputSize(ctLen))
        var written = cipher.processBytes(combined, NONCE_SIZE, ctLen, outBuf, 0)
        written += cipher.doFinal(outBuf, written)
        return if (written == outBuf.size) outBuf else outBuf.copyOfRange(0, written)
    }

    @Suppress("unused")
    private val anchor: Any = arrayOf(ChaCha7539Engine::class.java, Poly1305::class.java, ParametersWithIV::class.java)

    companion object {
        const val KEY_SIZE = 32
        const val NONCE_SIZE = 12
        const val TAG_SIZE = 16
        /** AEAD overhead per packet: nonce + tag. */
        const val OVERHEAD = NONCE_SIZE + TAG_SIZE  // 28
    }
}
