package ru.vertices.android.core.util

/** Lowercase hex encoder/decoder. Mirrors Swift `Data.hexString` / `Data(hexString:)`. */
object HexCodec {

    private val HEX_CHARS = "0123456789abcdef".toCharArray()

    fun encode(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val v = bytes[i].toInt() and 0xFF
            out[i * 2]     = HEX_CHARS[v ushr 4]
            out[i * 2 + 1] = HEX_CHARS[v and 0x0F]
        }
        return String(out)
    }

    fun decode(s: String): ByteArray {
        val clean = s.trim()
        require(clean.length % 2 == 0) { "hex string must be even length" }
        val out = ByteArray(clean.length / 2)
        for (i in out.indices) {
            val hi = Character.digit(clean[i * 2], 16)
            val lo = Character.digit(clean[i * 2 + 1], 16)
            require(hi >= 0 && lo >= 0) { "invalid hex char at offset ${i * 2}" }
            out[i] = ((hi shl 4) or lo).toByte()
        }
        return out
    }
}

fun ByteArray.toHex(): String = HexCodec.encode(this)
fun String.hexToBytes(): ByteArray = HexCodec.decode(this)
