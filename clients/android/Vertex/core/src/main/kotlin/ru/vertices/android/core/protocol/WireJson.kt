package ru.vertices.android.core.protocol

import kotlinx.serialization.json.Json

/**
 * The single Json instance used to encode/decode wire-protocol messages.
 *
 * - `ignoreUnknownKeys` — exits may grow new fields (e.g. capacity hints, extra
 *   diagnostic counters) and clients on older versions must not error out on them.
 * - `encodeDefaults = false` — we don't emit nullable fields that are null,
 *   matching Go `encoding/json` with `omitempty` and Swift `JSONEncoder` default.
 * - `explicitNulls = false` — same goal: never serialize `"id_sig": null` etc.,
 *   even when the field is explicitly null in Kotlin.
 */
val WireJson: Json = Json {
    ignoreUnknownKeys = true
    encodeDefaults    = false
    explicitNulls     = false
    isLenient         = false
}
