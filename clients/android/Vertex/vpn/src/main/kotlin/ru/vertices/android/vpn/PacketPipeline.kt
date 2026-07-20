package ru.vertices.android.vpn

import android.os.ParcelFileDescriptor
import ru.vertices.android.core.crypto.SessionCrypto
import timber.log.Timber
import java.io.FileInputStream
import java.io.FileOutputStream
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * Drives the two TUN <-> MQTT packet flows.
 *
 *  - `up`   thread: read IP packets from TUN, ChaCha20-Poly1305 seal, publish.
 *  - `down` thread: subscribe → open → write to TUN.
 *
 * `setSession()` lets the engine swap the crypto session without restarting the
 * threads (used on exit auto-switch in Phase 2+); for Phase 1 we set it once
 * after handshake and never change it.
 *
 * IP version filter: we silently drop IPv6 packets on the read side (parity
 * with iOS Phase 1; the tunnel is IPv4-only). `version` is the upper nibble
 * of byte 0.
 */
internal class PacketPipeline(
    private val tun: ParcelFileDescriptor,
    private val publishUpload: (payload: ByteArray) -> Unit,
) {

    fun interface SubscribeRegistrar {
        /** Register a download handler. The pipeline calls this once on start. */
        fun register(downloadHandler: (payload: ByteArray) -> Unit)
    }

    private val crypto = AtomicReference<SessionCrypto?>(null)
    private val running = AtomicReference(false)
    private var upThread: Thread? = null
    private var downCount = AtomicLong(0)

    val bytesUp   = AtomicLong(0)
    val bytesDown = AtomicLong(0)
    val packetsUp = AtomicLong(0)
    val packetsDown = AtomicLong(0)
    val decryptErrors = AtomicLong(0)

    fun setSession(s: SessionCrypto) {
        crypto.set(s)
    }

    fun start(register: SubscribeRegistrar) {
        if (!running.compareAndSet(false, true)) return

        // download direction — handler invoked by MqttTransport on its single thread
        register.register { sealed -> handleDownload(sealed) }

        // upload direction — dedicated blocking thread on TUN read
        val tunIn = FileInputStream(tun.fileDescriptor)
        val t = Thread({ runUpLoop(tunIn) }, "vtx-tun-up").apply { isDaemon = true }
        upThread = t
        t.start()
    }

    fun stop() {
        running.set(false)
        // Closing the FD is the only reliable way to break the blocking read.
        try { tun.close() } catch (_: Throwable) {}
        upThread?.let {
            try { it.join(2000) } catch (_: Throwable) {}
        }
        upThread = null
    }

    private fun runUpLoop(input: FileInputStream) {
        val buf = ByteArray(MTU + RAW_IP_HEADER_SLACK)
        try {
            while (running.get()) {
                val n = try { input.read(buf) } catch (_: Throwable) { -1 }
                if (n <= 0) {
                    if (running.get()) Timber.tag(TAG).w("TUN read returned $n — pipeline stopping")
                    return
                }
                val packet = buf.copyOfRange(0, n)
                if (!isIPv4(packet)) continue

                val s = crypto.get() ?: continue   // dropping until handshake completes
                val sealed = try { s.seal(packet) } catch (e: Throwable) {
                    Timber.tag(TAG).w(e, "seal failed; dropping packet")
                    continue
                }
                // Re-check running before publishing — there's a small window
                // where stop() flipped the flag while we were in seal(), and
                // pushing one last packet through MqttTransport after teardown
                // shows up as a ghost frame in stats and (worse) wakes the
                // scheduler thread we're trying to drain.
                if (!running.get()) return
                publishUpload(sealed)
                bytesUp.addAndGet(n.toLong())
                packetsUp.incrementAndGet()
            }
        } catch (t: Throwable) {
            if (running.get()) Timber.tag(TAG).e(t, "up loop crashed")
        }
    }

    private fun handleDownload(sealed: ByteArray) {
        if (!running.get()) return
        val s = crypto.get() ?: return
        val plain = try {
            s.open(sealed)
        } catch (e: Throwable) {
            val n = decryptErrors.incrementAndGet()
            if (n <= 5) Timber.tag(TAG).w(e, "decrypt failed (#$n)")
            return
        }
        if (!isIPv4(plain)) return

        // Single writer (this MQTT-callback thread) — no synchronization needed
        // beyond what the FD gives us, but instantiate the FOS once.
        val out = downOut
        try {
            out.write(plain)
            bytesDown.addAndGet(plain.size.toLong())
            packetsDown.incrementAndGet()
        } catch (t: Throwable) {
            Timber.tag(TAG).w(t, "TUN write failed")
        }

        downCount.incrementAndGet()
    }

    private val downOut: FileOutputStream by lazy {
        FileOutputStream(tun.fileDescriptor)
    }

    private fun isIPv4(packet: ByteArray): Boolean {
        if (packet.isEmpty()) return false
        return ((packet[0].toInt() ushr 4) and 0x0F) == 4
    }

    companion object {
        private const val TAG = "vtx-pipe"
        // Why 1300 and not 1500 (which is what iOS/macOS settle on):
        //
        //   inner IP packet  + ChaCha20-Poly1305 overhead (12 nonce + 16 tag = 28)
        //   wrapped as MQTT PUBLISH (≈ 10 B header)
        //   over TLS records (≈ 5–37 B/record)
        //   over TCP/IP/Wi-Fi (40 B headers, 1500 B physical MTU)
        //
        // With inner=1500 the encrypted blob spills over one Wi-Fi frame and
        // gets segmented at the TCP layer, which is fine — *if* PMTU works
        // end-to-end. In practice ICMP "fragmentation needed" is dropped by
        // most RU mobile/landline ISPs (DPI rule) so PMTU discovery
        // black-holes large flows: small probes and ifconfig.me succeed,
        // TLS handshakes to google.com / play.google.com / Android's
        // captivecheck endpoint stall after the first ClientHello segment.
        // Symptom: "ping есть, страница не грузится, рядом с Wi-Fi крестик".
        //
        // iOS/macOS skirt this because XNU's TCP stack opportunistically
        // clamps MSS down on the tunnel interface; Linux on Android does
        // not, so the tunnel has to advertise a smaller MTU itself. 1300 is
        // the conservative floor used by WireGuard / OpenVPN by default —
        // leaves >180 B of headroom per frame for every layer above TCP.
        const val MTU = 1300
        // Read buffer headroom over MTU. Linux TUN delivers a raw IP packet
        // ≤ MTU bytes per read — no AF prefix — but we keep a small slack so
        // an over-sized frame from a misbehaving sender shows up as `n > MTU`
        // we can detect and drop, instead of being silently truncated to MTU.
        private const val RAW_IP_HEADER_SLACK = 16
    }
}
