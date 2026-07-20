package ru.vertices.android.core.mqtt

import ru.vertices.android.core.config.BrokerUrl
import java.io.Closeable

/**
 * Byte transport beneath the MQTT framing. Two implementations:
 *
 * - [TlsMqttSocket] — `mqtts://` and `mqtt://` over a single long-lived
 *   `(SSL)Socket`. The MQTT codec frames packets directly on the byte stream.
 * - [WssMqttSocket] — `wss://` and `ws://` via OkHttp WebSocket. Each MQTT
 *   packet is sent as a single binary frame; on receive, accumulate into a
 *   buffer and feed the codec's `tryDecode` loop (multiple MQTT packets may
 *   arrive in one frame).
 *
 * `protect()` (Android `VpnService.protect(Socket)`) MUST be called by the
 * caller right after the underlying socket is created — otherwise broker
 * traffic recurses through the tunnel. We inject the protect callback at
 * connect time so this module stays free of `VpnService` imports.
 */
interface MqttSocket : Closeable {

    /** Remote broker we're talking to. */
    val broker: BrokerUrl

    /** Open the TCP/TLS/WS connection and become ready for [send]. */
    fun connect(listener: Listener)

    /** Send one MQTT packet (full byte array — already framed by codec). */
    fun send(packet: ByteArray)

    /** Close the underlying socket. Idempotent. */
    override fun close()

    /**
     * Notifications from the socket back to the connection layer.
     *
     * - [onBytes] — bytes have arrived. The connection accumulates and runs
     *   [MqttPacketCodec.tryDecode] in a loop.
     * - [onClosed] — the socket is no longer usable. `cause` may be null on
     *   normal close. `pathDead` indicates the underlying network path is
     *   gone (TCP `ENOTCONN`, peer reset on dead interface) — the transport
     *   uses this to escalate to a fresh socket on the new default path.
     */
    interface Listener {
        fun onConnected()
        fun onBytes(data: ByteArray)
        fun onClosed(cause: Throwable?, pathDead: Boolean)
    }
}

/** Optional VpnService.protect() hook injected from `:vpn`. */
fun interface SocketProtector {
    fun protect(socket: java.net.Socket): Boolean
}
