package ru.vertices.android.core.protocol

/**
 * MQTT topic builders, mirror of Go `pkg/protocol` and Swift `VertexCore.Topics`.
 *
 *     vpn/{exit}/{name}/out      client → exit (encrypted IP packets)
 *     vpn/{exit}/{name}/in       exit   → client (encrypted IP packets)
 *     vpn/{exit}/{name}/control  exit   → client (assign / errors)
 *     vpn/{exit}/control/join    client → exit (handshake)
 *     discovery/exits/{exit}     exit broadcasts (retained)
 */
object Topics {

    fun upload(exit: String, name: String): String   = "vpn/$exit/$name/out"
    fun download(exit: String, name: String): String = "vpn/$exit/$name/in"
    fun control(exit: String, name: String): String  = "vpn/$exit/$name/control"
    fun join(exit: String): String                   = "vpn/$exit/control/join"
    fun discovery(exit: String): String              = "discovery/exits/$exit"

    /** Wildcard download — used when client wants any exit. */
    fun downloadAny(name: String): String  = "vpn/+/$name/in"
    /** Wildcard control. */
    fun controlAny(name: String): String   = "vpn/+/$name/control"
    /** Subscribe to all exit heartbeats. */
    const val DISCOVERY_ALL: String = "discovery/exits/+"

    /**
     * MQTT topic match (`+` single-level, `#` multi-level at end).
     * Mirrors `MQTTTransport.topicMatches` in Swift.
     */
    fun matches(topic: String, pattern: String): Boolean {
        val topicParts = topic.split('/')
        val patternParts = pattern.split('/')
        var ti = 0
        var pi = 0
        while (pi < patternParts.size) {
            val pp = patternParts[pi]
            if (pp == "#") return true
            if (ti >= topicParts.size) return false
            if (pp != "+" && pp != topicParts[ti]) return false
            ti++; pi++
        }
        return ti == topicParts.size
    }
}
