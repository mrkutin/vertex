package ru.vertices.android.core.mqtt

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import ru.vertices.android.core.config.BrokerUrl
import timber.log.Timber
import java.util.concurrent.TimeUnit

/**
 * MQTT-over-WebSocket socket (port 443 DPI fallback). Each MQTT packet is sent
 * as one binary WebSocket frame. On receive, the listener accumulates bytes and
 * the connection layer runs `MqttPacketCodec.tryDecode` in a loop — multiple
 * MQTT packets can arrive in one WS frame.
 *
 * OkHttp owns the I/O threads; the listener is invoked off the calling thread.
 *
 * VpnService.protect(): OkHttp uses `SocketFactory` internally. We can't reach
 * the raw Socket cleanly. Phase 1 doesn't need this because brokers are
 * `mqtts://` (broker bypass via [TlsMqttSocket.protector]); WSS is Phase 2+
 * where we'll wire a custom SocketFactory.
 */
internal class WssMqttSocket(
    override val broker: BrokerUrl,
    private val client: OkHttpClient = defaultClient(),
) : MqttSocket {

    init {
        require(broker.scheme == BrokerUrl.Scheme.WS || broker.scheme == BrokerUrl.Scheme.WSS) {
            "WssMqttSocket: expected ws(s)://, got ${broker.scheme.raw}"
        }
    }

    @Volatile private var ws: WebSocket? = null
    @Volatile private var listener: MqttSocket.Listener? = null
    @Volatile private var closed = false

    override fun connect(listener: MqttSocket.Listener) {
        check(ws == null) { "already connected" }
        this.listener = listener

        val req = Request.Builder()
            .url("${broker.scheme.raw}://${broker.host}:${broker.port}/mqtt")
            .header("Sec-WebSocket-Protocol", "mqtt")
            .build()

        ws = client.newWebSocket(req, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (!closed) listener.onConnected()
            }
            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                if (!closed) listener.onBytes(bytes.toByteArray())
            }
            override fun onMessage(webSocket: WebSocket, text: String) {
                Timber.tag(TAG).w("ignoring text frame on MQTT WS (${text.length} chars)")
            }
            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(1000, null)
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (!closed) listener.onClosed(cause = null, pathDead = false)
            }
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Timber.tag(TAG).w(t, "WS failure for ${broker.host}")
                if (!closed) listener.onClosed(cause = t, pathDead = isPathDead(t))
            }
        })
    }

    override fun send(packet: ByteArray) {
        val w = ws ?: throw IllegalStateException("ws not open")
        if (!w.send(packet.toByteString())) {
            // OkHttp returns false when send queue is full — rare for our trickle.
            throw java.io.IOException("WebSocket send queue rejected packet")
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        listener = null
        ws?.close(1000, null)
        ws = null
    }

    private fun isPathDead(t: Throwable): Boolean {
        val msg = (t.message ?: "").lowercase()
        return msg.contains("enotconn") ||
            msg.contains("econnreset") ||
            msg.contains("connection reset") ||
            msg.contains("connection abort")
    }

    companion object {
        private const val TAG = "vtx-mqtt-sock-ws"

        private fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS) // long-lived
            .pingInterval(0, TimeUnit.SECONDS)      // we do MQTT-level ping ourselves
            .build()
    }
}
