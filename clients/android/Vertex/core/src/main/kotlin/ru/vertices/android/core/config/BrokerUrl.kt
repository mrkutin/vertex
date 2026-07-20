package ru.vertices.android.core.config

import java.net.InetAddress

/**
 * Parsed broker URL. Accepts `mqtt(s)://host[:port]` and `ws(s)://host[:port]`.
 * Mirror of Swift `BrokerURL`.
 */
data class BrokerUrl(
    val scheme: Scheme,
    val host: String,
    val port: Int,
) {
    enum class Scheme(val raw: String, val defaultPort: Int) {
        MQTT("mqtt",   1883),
        MQTTS("mqtts", 8883),
        WS("ws",       80),
        WSS("wss",     443);

        companion object {
            fun fromRaw(raw: String): Scheme? =
                values().firstOrNull { it.raw == raw.lowercase() }
        }
    }

    val isTls: Boolean get() = scheme == Scheme.MQTTS || scheme == Scheme.WSS
    val isWebSocket: Boolean get() = scheme == Scheme.WS || scheme == Scheme.WSS

    val urlString: String get() = "${scheme.raw}://$host:$port"

    /** Resolve broker hostname to IPv4 addresses (for VpnService.protect bypass logic). */
    fun resolveIPv4(): List<String> = try {
        InetAddress.getAllByName(host)
            .filter { it.address.size == 4 }
            .map { it.hostAddress!! }
            .distinct()
    } catch (_: Throwable) {
        emptyList()
    }

    companion object {
        private val URL_RE = Regex("""^(mqtt|mqtts|ws|wss)://([^:/?#]+)(?::(\d+))?$""", RegexOption.IGNORE_CASE)

        /** Parse a broker URL string. Returns null on malformed input. */
        fun parse(s: String): BrokerUrl? {
            val m = URL_RE.matchEntire(s.trim()) ?: return null
            val scheme = Scheme.fromRaw(m.groupValues[1]) ?: return null
            val host = m.groupValues[2]
            if (host.isEmpty()) return null
            val port = m.groupValues[3].takeIf { it.isNotEmpty() }?.toIntOrNull() ?: scheme.defaultPort
            if (port !in 1..65535) return null
            return BrokerUrl(scheme, host, port)
        }
    }
}
