package ru.vertices.android.core.util

import java.util.Base64

/** Standard base64 (RFC 4648 §4, with padding) — same encoding the Go and Swift sides use
 *  for X25519 pubkeys / identity proofs in JoinMessage / AssignMessage / DiscoveryHeartbeat. */
object Base64Codec {

    private val encoder: Base64.Encoder = Base64.getEncoder()
    private val decoder: Base64.Decoder = Base64.getDecoder()

    fun encode(bytes: ByteArray): String = encoder.encodeToString(bytes)

    fun decode(s: String): ByteArray = decoder.decode(s.trim())
}

fun ByteArray.toBase64(): String = Base64Codec.encode(this)
fun String.base64ToBytes(): ByteArray = Base64Codec.decode(this)
