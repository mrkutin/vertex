package ru.vertices.android.core.crypto

import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters

/**
 * HKDF-SHA256 (Extract-then-Expand), byte-exact with Go `golang.org/x/crypto/hkdf`
 * and Swift `SharedSecret.hkdfDerivedSymmetricKey(using: SHA256.self, salt:, sharedInfo:, outputByteCount:)`.
 */
object Hkdf {

    fun deriveSha256(
        ikm: ByteArray,
        salt: ByteArray,
        info: ByteArray,
        outputBytes: Int,
    ): ByteArray {
        require(outputBytes > 0) { "outputBytes must be positive" }
        val gen = HKDFBytesGenerator(SHA256Digest())
        gen.init(HKDFParameters(ikm, salt, info))
        val out = ByteArray(outputBytes)
        gen.generateBytes(out, 0, outputBytes)
        return out
    }
}
