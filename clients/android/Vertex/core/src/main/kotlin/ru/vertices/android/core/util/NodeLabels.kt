package ru.vertices.android.core.util

import java.net.URI

/**
 * Vertex/Edge label helpers — graph-theoretic naming per design.
 * Mirror of `clients/shared/VertexCore/Sources/VertexCore/Util/NodeLabels.swift`.
 *
 * Vertices (brokers) → `V₀ · NAME`, edges (exit nodes) → `E₀ · NAME`.
 * Subscripts use Unicode code points U+2080…U+2089. The middle dot is U+00B7.
 */
object NodeLabels {
    private val SUBSCRIPTS = arrayOf(
        "₀", "₁", "₂", "₃", "₄",
        "₅", "₆", "₇", "₈", "₉",
    )

    private fun subscriptGlyph(index: Int): String =
        if (index in SUBSCRIPTS.indices) SUBSCRIPTS[index] else index.toString()

    data class VertexLabel(val shortName: String, val code: String)
    data class EdgeLabel(val display: String, val code: String)

    /** E.g. ("mqtt-yc.vertices.ru", 0) → ("YC", "V₀ · YC") */
    fun vertexLabel(host: String, index: Int): VertexLabel {
        val name = vertexShortName(host)
        val code = "V${subscriptGlyph(index)} · $name"
        return VertexLabel(name, code)
    }

    /**
     * E.g. ("aws", 0, "Toronto, Canada") → ("Toronto, Canada", "E₀ · AWS").
     *
     * `displayOverride` carries the city/country string fetched from a TXT
     * record on the SRV target host (see `SrvDiscovery.exitDisplayNames`).
     * When absent, the display falls back to uppercased ID — adding a new
     * exit only requires SRV + TXT in DNS, never an app update.
     */
    fun edgeLabel(id: String, index: Int, displayOverride: String? = null): EdgeLabel {
        val display = when {
            !displayOverride.isNullOrBlank() -> displayOverride
            id.lowercase() == "auto"         -> "Auto"
            else                             -> id.uppercase()
        }
        val code = "E${subscriptGlyph(index)} · ${id.uppercase()}"
        return EdgeLabel(display, code)
    }

    /**
     * Unique vertex hosts in encounter order.
     * E.g. ["mqtts://mqtt-yc...:8883", "wss://mqtt-yc...:443", "mqtts://mqtt-sber...:8883"]
     * → ["mqtt-yc.vertices.ru", "mqtt-sber.vertices.ru"]
     */
    fun uniqueHosts(urls: List<String>): List<String> {
        val seen = LinkedHashSet<String>()
        for (url in urls) {
            val h = host(url) ?: continue
            seen.add(h)
        }
        return seen.toList()
    }

    /**
     * Schemes (uppercased: MQTTS, WSS, MQTT) for a given host across the URL list,
     * preserving encounter order so primary protocol comes first.
     */
    fun protocols(host: String, urls: List<String>): List<String> {
        val seen = LinkedHashSet<String>()
        for (url in urls) {
            if (host(url) != host) continue
            val scheme = scheme(url) ?: continue
            seen.add(scheme.uppercase())
        }
        return seen.toList()
    }

    /** Extracts the host component from a URL string, or null if unparseable. */
    fun host(url: String): String? = runCatching {
        URI(url).host?.takeIf { it.isNotBlank() }
    }.getOrNull()

    /** Extracts the scheme (e.g. "mqtts"), or null if unparseable. */
    fun scheme(url: String): String? = runCatching {
        URI(url).scheme?.takeIf { it.isNotBlank() }
    }.getOrNull()

    /** Port from URL, or null if unparseable / unspecified. */
    fun port(url: String): Int? = runCatching {
        URI(url).port.takeIf { it > 0 }
    }.getOrNull()

    /**
     * Short name derived purely from the hostname — the first DNS label with
     * any `mqtt-` prefix stripped, uppercased. `mqtt-yc.vertices.ru` → `YC`,
     * `mqtt-sber.vertices.ru` → `SBER`, `broker.example.com` → `BROKER`.
     *
     * No special-case table: every broker the SRV record returns gets a
     * sensible label without an app update, as long as ops follow the
     * `mqtt-{shortname}.{zone}` naming convention.
     */
    private fun vertexShortName(host: String): String {
        val firstLabel = host.split(".").firstOrNull().orEmpty()
        val stripped = if (firstLabel.lowercase().startsWith("mqtt-")) {
            firstLabel.substring("mqtt-".length)
        } else firstLabel
        return stripped.uppercase()
    }
}
