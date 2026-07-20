package ru.vertices.android.core.mqtt

import ru.vertices.android.core.config.BrokerUrl
import timber.log.Timber
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

/**
 * One MQTT 5.0 connection on top of [MqttSocket]. Mirror of Swift `MQTTConnection`.
 *
 * Responsibilities:
 * 1. Drive socket → MQTT framing → CONNECT → CONNACK → ready.
 * 2. Send PINGREQ on cadence and bail when PINGRESP doesn't come back.
 * 3. Surface state transitions to [MqttTransport] via [Listener].
 *
 * NOT responsible for: reconnection, broker selection, resubscribe — that's
 * [MqttTransport]'s job.
 */
internal class MqttConnection(
    private val socket: MqttSocket,
    private val clientId: String,
    private val username: String,
    private val password: String,
    private val keepAliveSeconds: Int = 20,
    private val scheduler: ScheduledExecutorService = defaultScheduler(),
) {

    /** Disconnect cause categories surfaced to the transport. */
    sealed interface Event {
        data object Connected : Event
        /**
         * @param connackReason non-null when the broker rejected our CONNECT
         *   (auth failure, banned, server unavailable). Transport short-circuits
         *   reconnect — retrying with the same creds will keep failing.
         * @param linkDead the underlying network path is gone (PINGRESP timeout
         *   or socket EBADF / ENOTCONN). Transport escalates to a fresh socket
         *   on the new default route rather than retrying same-process.
         */
        data class Disconnected(
            val cause: Throwable?,
            val linkDead: Boolean,
            val connackReason: Int? = null,
        ) : Event
    }

    fun interface Listener {
        fun onEvent(event: Event)
    }

    fun interface PublishHandler {
        fun onPublish(topic: String, payload: ByteArray)
    }

    @Volatile private var listener: Listener? = null
    @Volatile private var publishHandler: PublishHandler? = null

    @Volatile private var connected = false
    @Volatile private var hasBeenReady = false
    private var receiveBuffer = ByteArray(0)
    private var nextPacketId = 1

    private var pingTask: ScheduledFuture<*>? = null
    private var pingResponseTask: ScheduledFuture<*>? = null
    @Volatile private var pingResponsePending = false

    val broker: BrokerUrl get() = socket.broker
    val isConnected: Boolean get() = connected

    fun connect(listener: Listener, onPublish: PublishHandler) {
        this.listener = listener
        this.publishHandler = onPublish

        socket.connect(object : MqttSocket.Listener {
            override fun onConnected() {
                Timber.tag(TAG).i("transport ready, sending CONNECT to ${broker.host}")
                trySend(MqttPacketCodec.encodeConnect(
                    ConnectPacket(
                        clientId = clientId,
                        username = username.takeIf { it.isNotEmpty() },
                        password = password.takeIf { it.isNotEmpty() },
                        keepAlive = keepAliveSeconds,
                        cleanStart = true,
                        sessionExpiryInterval = 0,
                    )
                ))
            }
            override fun onBytes(data: ByteArray) {
                receiveBuffer += data
                drainReceiveBuffer()
            }
            override fun onClosed(cause: Throwable?, pathDead: Boolean) {
                handleDisconnect(cause, linkDead = pathDead || (hasBeenReady && cause != null), null)
            }
        })
    }

    fun publish(topic: String, payload: ByteArray, retain: Boolean = false, messageExpirySeconds: Int? = 10) {
        if (!connected) return
        val pkt = PublishPacket(topic, payload, retain, messageExpirySeconds)
        trySend(MqttPacketCodec.encodePublish(pkt))
    }

    fun subscribe(topics: List<String>) {
        if (!connected || topics.isEmpty()) return
        val packetId = nextPacketId.also {
            nextPacketId = if (it == 0xFFFF) 1 else it + 1
        }
        trySend(MqttPacketCodec.encodeSubscribe(SubscribePacket(packetId, topics)))
    }

    /** Force a fresh PINGREQ ahead of the regular cadence. No-op if not connected
     *  or if a ping is already in flight. */
    fun pingNow() {
        if (!connected || pingResponsePending) return
        sendPing()
        startPingTimer()
    }

    /** Graceful MQTT DISCONNECT + close socket. */
    fun disconnect() {
        if (connected) {
            try { socket.send(MqttPacketCodec.encodeDisconnect()) } catch (_: Throwable) {}
        }
        teardown(null, linkDead = false, connackReason = null, emit = true)
    }

    // ---- Internal ----

    private fun drainReceiveBuffer() {
        while (true) {
            val triple = try {
                MqttPacketCodec.tryDecode(receiveBuffer)
            } catch (e: MqttCodecError) {
                Timber.tag(TAG).e(e, "codec error — closing")
                handleDisconnect(e, linkDead = false, connackReason = null)
                return
            } ?: return  // need more bytes
            val (type, packetData, consumed) = triple
            // Slice off the consumed prefix (allocation-light: just shift via copy).
            receiveBuffer = if (consumed == receiveBuffer.size) {
                ByteArray(0)
            } else {
                receiveBuffer.copyOfRange(consumed, receiveBuffer.size)
            }
            handlePacket(type, packetData)
            if (!connected && type != MqttPacketType.CONNACK) {
                // Disconnect was triggered by a packet; stop draining.
                return
            }
        }
    }

    private fun handlePacket(type: MqttPacketType, data: ByteArray) {
        when (type) {
            MqttPacketType.CONNACK -> {
                val ack = try { MqttPacketCodec.decodeConnack(data) } catch (e: Throwable) {
                    handleDisconnect(e, linkDead = false, connackReason = null); return
                }
                if (ack.isSuccess) {
                    Timber.tag(TAG).i("CONNACK success (sessionPresent=${ack.sessionPresent})")
                    connected = true
                    hasBeenReady = true
                    startPingTimer()
                    listener?.onEvent(Event.Connected)
                } else {
                    Timber.tag(TAG).w("CONNACK rejected: ${ack.reasonString} (code=${ack.reasonCode})")
                    handleDisconnect(MqttCodecError.ConnackFailed(ack.reasonString),
                        linkDead = false, connackReason = ack.reasonCode)
                }
            }
            MqttPacketType.PUBLISH -> {
                val pub = try { MqttPacketCodec.decodePublish(data) } catch (e: Throwable) {
                    Timber.tag(TAG).w(e, "PUBLISH decode error"); return
                }
                publishHandler?.onPublish(pub.topic, pub.payload)
            }
            MqttPacketType.SUBACK -> {
                val ack = try { MqttPacketCodec.decodeSuback(data) } catch (_: Throwable) { return }
                if (!ack.allSuccess) Timber.tag(TAG).w("SUBACK partial failure: ${ack.reasonCodes}")
            }
            MqttPacketType.PINGRESP -> {
                pingResponsePending = false
                pingResponseTask?.cancel(false)
                pingResponseTask = null
            }
            MqttPacketType.DISCONNECT -> {
                Timber.tag(TAG).i("received DISCONNECT from broker")
                handleDisconnect(cause = null, linkDead = false, connackReason = null)
            }
            else -> { /* ignore unsupported */ }
        }
    }

    private fun startPingTimer() {
        cancelPingTasks()
        val interval = (keepAliveSeconds - 5).coerceAtLeast(5).toLong()
        pingTask = scheduler.scheduleAtFixedRate(
            ::sendPing, interval, interval, TimeUnit.SECONDS,
        )
    }

    private fun sendPing() {
        if (!connected) return
        if (pingResponsePending) {
            // Defence-in-depth: pingResponseTask should already have fired.
            Timber.tag(TAG).w("PINGRESP still pending at next PINGREQ — declaring link dead")
            handleDisconnect(MqttCodecError.ConnackFailed("PINGRESP timeout"), linkDead = true, connackReason = null)
            return
        }
        pingResponsePending = true
        try { socket.send(MqttPacketCodec.encodePingReq()) } catch (e: Throwable) {
            handleDisconnect(e, linkDead = true, connackReason = null); return
        }
        // 5-second deadline for PINGRESP. If broker doesn't reply, the link is dead.
        pingResponseTask = scheduler.schedule({
            if (pingResponsePending) {
                Timber.tag(TAG).w("PINGRESP not received within ${PING_RESPONSE_TIMEOUT_SEC}s — link dead")
                handleDisconnect(MqttCodecError.ConnackFailed("PINGRESP timeout"),
                    linkDead = true, connackReason = null)
            }
        }, PING_RESPONSE_TIMEOUT_SEC, TimeUnit.SECONDS)
    }

    private fun cancelPingTasks() {
        pingTask?.cancel(false); pingTask = null
        pingResponseTask?.cancel(false); pingResponseTask = null
        pingResponsePending = false
    }

    private fun handleDisconnect(cause: Throwable?, linkDead: Boolean, connackReason: Int?) {
        teardown(cause, linkDead, connackReason, emit = true)
    }

    private fun teardown(cause: Throwable?, linkDead: Boolean, connackReason: Int?, emit: Boolean) {
        val wasConnected = connected
        connected = false
        cancelPingTasks()
        try { socket.close() } catch (_: Throwable) {}
        // Atomically nil out the listener so a late callback doesn't double-emit.
        val l = listener
        listener = null
        publishHandler = null
        if (emit && (wasConnected || cause != null)) {
            l?.onEvent(Event.Disconnected(cause, linkDead, connackReason))
        }
    }

    private fun trySend(packet: ByteArray) {
        try {
            socket.send(packet)
        } catch (e: Throwable) {
            Timber.tag(TAG).w(e, "send failed — declaring link dead")
            handleDisconnect(e, linkDead = true, connackReason = null)
        }
    }

    companion object {
        private const val TAG = "vtx-mqtt-conn"
        private const val PING_RESPONSE_TIMEOUT_SEC = 5L

        private fun defaultScheduler(): ScheduledExecutorService =
            Executors.newSingleThreadScheduledExecutor { r ->
                Thread(r, "vtx-mqtt-sched").apply { isDaemon = true }
            }
    }
}
