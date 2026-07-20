package ru.vertices.android.core.ipc

import kotlinx.serialization.Serializable

/** Cumulative tunnel byte/packet counters since the last connect. */
@Serializable
data class TunnelStats(
    val bytesUp: Long = 0,
    val bytesDown: Long = 0,
    val packetsUp: Long = 0,
    val packetsDown: Long = 0,
) {
    companion object {
        val ZERO: TunnelStats = TunnelStats()
    }
}
