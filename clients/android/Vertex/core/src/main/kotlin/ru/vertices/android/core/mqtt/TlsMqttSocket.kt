package ru.vertices.android.core.mqtt

import ru.vertices.android.core.config.BrokerUrl
import timber.log.Timber
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import javax.net.ssl.SSLSocketFactory

/**
 * MQTT-over-TLS (or plain TCP) socket. One long-lived stream — the MQTT codec
 * frames packets directly on the byte stream. Mirrors the Swift NWConnection
 * approach used in `VertexCore.MQTTConnection`.
 *
 * Threading: a single dedicated background thread runs the blocking `read()`
 * loop and owns all writes. The listener is invoked on that thread; callers
 * must not perform expensive work in the callbacks.
 */
internal class TlsMqttSocket(
    override val broker: BrokerUrl,
    private val protector: SocketProtector? = null,
    private val connectTimeoutMs: Int = 15_000,
    private val socketFactory: SSLSocketFactory = SSLSocketFactory.getDefault() as SSLSocketFactory,
) : MqttSocket {

    init {
        require(broker.scheme == BrokerUrl.Scheme.MQTT || broker.scheme == BrokerUrl.Scheme.MQTTS) {
            "TlsMqttSocket: expected mqtt(s)://, got ${broker.scheme.raw}"
        }
    }

    @Volatile private var socket: Socket? = null
    @Volatile private var rxThread: Thread? = null
    @Volatile private var listener: MqttSocket.Listener? = null
    @Volatile private var closed = false

    override fun connect(listener: MqttSocket.Listener) {
        check(socket == null) { "already connected" }
        this.listener = listener

        val t = Thread({ runConnect() }, "vtx-mqtt-rx-${broker.host}")
        t.isDaemon = true
        rxThread = t
        t.start()
    }

    private fun runConnect() {
        val raw = Socket()
        try {
            // Protect the raw socket BEFORE connect() — once the kernel binds the
            // socket to the TUN interface, broker traffic loops through the
            // tunnel and instantly deadlocks the handshake.
            protector?.let {
                if (!it.protect(raw)) {
                    Timber.tag(TAG).w("VpnService.protect() returned false for ${broker.host}")
                }
            }
            raw.tcpNoDelay = true
            raw.connect(InetSocketAddress(broker.host, broker.port), connectTimeoutMs)

            val sock: Socket = if (broker.isTls) {
                (socketFactory.createSocket(raw, broker.host, broker.port, true) as javax.net.ssl.SSLSocket).apply {
                    enableSessionCreation = true
                    // Standard SNI: SSLParameters.serverNames() — host comes through
                    // automatically via the layered constructor above. startHandshake()
                    // forces TLS now so the listener.onConnected() fires only after
                    // the handshake is done.
                    startHandshake()
                }
            } else {
                raw
            }

            socket = sock
            listener?.onConnected()
            readLoop(sock)
        } catch (t: Throwable) {
            Timber.tag(TAG).w(t, "connect failed for ${broker.host}:${broker.port}")
            try { raw.close() } catch (_: Throwable) {}
            if (!closed) {
                listener?.onClosed(cause = t, pathDead = isPathDead(t))
            }
        }
    }

    private fun readLoop(sock: Socket) {
        val buf = ByteArray(8 * 1024)
        val input = sock.getInputStream()
        try {
            while (!closed) {
                val n = input.read(buf)
                if (n <= 0) {
                    listener?.onClosed(cause = null, pathDead = false)
                    return
                }
                listener?.onBytes(buf.copyOfRange(0, n))
            }
        } catch (t: Throwable) {
            if (!closed) {
                listener?.onClosed(cause = t, pathDead = isPathDead(t))
            }
        } finally {
            try { sock.close() } catch (_: Throwable) {}
        }
    }

    override fun send(packet: ByteArray) {
        val sock = socket ?: throw IOException("socket not connected")
        // SSLSocket OutputStream is thread-safe for the small writes we do (each
        // call corresponds to one full MQTT packet); guard with a lock for safety.
        synchronized(this) {
            sock.getOutputStream().apply {
                write(packet)
                flush()
            }
        }
    }

    override fun close() {
        if (closed) return
        closed = true
        listener = null
        try { socket?.close() } catch (_: Throwable) {}
        socket = null
    }

    /**
     * Heuristic for "the underlying network path is gone" — used to escalate
     * to a fresh socket on the new best path instead of looping on a dead
     * interface. We treat ECONNRESET / ENOTCONN / "connection abort" as
     * path-dead; SSL handshake errors are NOT path-dead (broker config issue).
     */
    private fun isPathDead(t: Throwable): Boolean {
        val msg = (t.message ?: "").lowercase()
        return msg.contains("enotconn") ||
            msg.contains("econnreset") ||
            msg.contains("connection reset") ||
            msg.contains("connection abort") ||
            msg.contains("software caused connection abort")
    }

    companion object { private const val TAG = "vtx-mqtt-sock" }
}
