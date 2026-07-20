package ru.vertices.android.core.probe

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Measures one TCP round-trip (SYN → SYN+ACK) to a host:port.
 *
 * Mirror of `TCPRTT.swift` — semantics MUST stay byte-equivalent with the
 * iOS / macOS shared `VertexCore.TCPRTT` and the Go reference
 * `pkg/probe.MeasureBroker`. Only the kernel handshake is timed; TLS and
 * WebSocket negotiation are excluded so a slow certificate chain does not
 * skew the network-latency reading.
 *
 * No `VpnService.protect` here: probes run BEFORE the TUN comes up, so
 * the system routing table is still intact and traffic reaches the
 * physical interface naturally.
 */
object TcpRtt {

    /**
     * Connect once and return the elapsed milliseconds.
     *
     * `Socket.use { … }` releases the descriptor on every exit path,
     * including `SocketTimeoutException` from `connect`. We wrap the
     * whole call in `runCatching` so timeouts and `ConnectException`s
     * surface as `Result.failure` instead of throwing — matches the
     * Swift `Result<Int, Error>` shape.
     */
    suspend fun measure(host: String, port: Int, timeoutMs: Long): Result<Int> =
        withContext(Dispatchers.IO) {
            runCatching {
                Socket().use { socket ->
                    val start = System.nanoTime()
                    socket.connect(InetSocketAddress(host, port), timeoutMs.toInt())
                    ((System.nanoTime() - start) / 1_000_000L).toInt()
                }
            }
        }
}
