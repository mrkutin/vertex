package ru.vertices.android.repository

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.datastore.preferences.preferencesDataStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.withContext
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton
import timber.log.Timber

/**
 * Single SRV record (RFC 2782).
 *
 * Lower priority is preferred; among equal priorities, higher weight wins.
 */
@Serializable
data class SrvRecord(
    val priority: Int,
    val weight: Int,
    val port: Int,
    val target: String,
) : Comparable<SrvRecord> {
    override fun compareTo(other: SrvRecord): Int =
        if (priority != other.priority) priority.compareTo(other.priority)
        else other.weight.compareTo(weight)
}

@Serializable
data class SrvDiscoveryResult(
    val domain: String,
    val backupDomain: String? = null,
    val brokers: List<SrvRecord>,
    val exits: List<SrvRecord>,
    /**
     * Per-exit display name read from a TXT record on the SRV target host
     * (e.g. `aws.exit.vertices.ru. IN TXT "Toronto, Canada"`). Key = exit
     * ID (first label of the SRV target). Missing entry → no city/country
     * available, UI falls back to uppercased ID. Defaulted for backward
     * compatibility with cached results from previous app versions.
     */
    val exitDisplayNames: Map<String, String> = emptyMap(),
    val updatedAtEpochMs: Long,
) {
    /** Broker URLs sorted by SRV priority. Port convention: 8883→mqtts, 443→wss, 1883→mqtt. */
    val brokerUrls: List<String> get() = brokers.map { r ->
        val scheme = when (r.port) {
            8883 -> "mqtts"
            443  -> "wss"
            1883 -> "mqtt"
            else -> "mqtt"
        }
        "$scheme://${r.target}:${r.port}"
    }

    /** Exit IDs extracted from SRV targets. Convention: "{id}.exit.{domain}" → first label. */
    val exitIds: List<String> get() = exits.map { r ->
        r.target.split('.').firstOrNull().orEmpty().ifEmpty { r.target }
    }

    /** True while cache age stays under the in-memory freshness window. */
    fun isFresh(nowEpochMs: Long = System.currentTimeMillis()): Boolean =
        (nowEpochMs - updatedAtEpochMs) in 0..CACHE_TTL_MS

    companion object {
        /**
         * In-memory freshness for SRV / exit lookups. Sized for SRV TTLs of
         * a few minutes amortized over background app sessions: a fresh app
         * launch within this window skips the DoH round-trip and uses the
         * cached answer; older than this and we re-resolve. iOS uses no
         * explicit TTL — cache lives in UserDefaults and is overwritten on
         * every successful resolve. We tighten to a finite window so the
         * client picks up broker churn within hours, not days.
         */
        const val CACHE_TTL_MS: Long = 6 * 60 * 60 * 1000L  // 6 hours
    }
}

private val Context.srvCacheDataStore by preferencesDataStore("vtx_srv_cache")

/**
 * Resolve `_mqtt._tcp.{domain}` and `_vtx-exit._tcp.{domain}` (with `_vtx-backup._tcp.{domain}`
 * fallback) via DNS-over-HTTPS — Cloudflare first, Google second. Mirror of
 * `clients/ios/Vertex/App/Services/SRVDiscovery.swift`.
 *
 * Caching strategy:
 *   - on success → persist last good answer (incl. backup domain)
 *   - primary failure → retry against the cached backup domain
 *   - both fail → return cached answer (regardless of age)
 *   - everything missing → return null
 */
@Singleton
class SrvDiscovery @Inject constructor(
    @ApplicationContext private val context: Context,
) {

    private val store = context.srvCacheDataStore
    private val cacheKey = stringPreferencesKey("srvDiscoveryCache")
    private val client by lazy {
        OkHttpClient.Builder()
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(5, TimeUnit.SECONDS)
            .build()
    }
    private val json = Json { ignoreUnknownKeys = true }

    suspend fun resolveWithFallback(domain: String): SrvDiscoveryResult? {
        runCatching { resolve(domain) }.getOrNull()?.let {
            saveCache(it)
            return it
        }
        Timber.tag(TAG).w("Primary domain %s failed", domain)

        loadCache()?.backupDomain?.takeIf { it.isNotBlank() }?.let { backup ->
            runCatching { resolve(backup) }.getOrNull()?.let {
                saveCache(it)
                Timber.tag(TAG).i("Resolved via backup %s", backup)
                return it
            }
            Timber.tag(TAG).w("Backup %s also failed", backup)
        }

        loadCache()?.takeIf { it.brokers.isNotEmpty() }?.let { cached ->
            Timber.tag(TAG).i("Using cached results")
            return cached
        }
        Timber.tag(TAG).e("All DNS discovery failed for %s", domain)
        return null
    }

    suspend fun resolve(domain: String): SrvDiscoveryResult = coroutineScope {
        val brokerJob = async(Dispatchers.IO) { lookup("_mqtt._tcp.$domain") }
        val exitJob = async(Dispatchers.IO) { lookupSafe("_vtx-exit._tcp.$domain") }
        val backupJob = async(Dispatchers.IO) { lookupSafe("_vtx-backup._tcp.$domain") }

        val brokers = brokerJob.await()
        val exits = exitJob.await()
        val backups = backupJob.await()
        require(brokers.isNotEmpty()) { "No SRV records for $domain" }

        val backupDomain = backups.sorted().firstOrNull()?.target?.trimEnd('.', ' ')

        // TXT lookup for each exit's SRV target host. Mapped to exit ID
        // (first label) so the UI can render "Toronto, Canada" alongside
        // the "aws" code without hardcoding the city table. Each query
        // runs in parallel; failures fall back to no entry, which makes
        // [NodeLabels.edgeLabel] render the uppercased ID instead.
        val displayJobs = exits.associate { rec ->
            val target = rec.target.trimEnd('.', ' ')
            val id = target.split('.').firstOrNull().orEmpty().ifEmpty { target }
            id to async(Dispatchers.IO) { lookupTxtSafe(target) }
        }
        val exitDisplayNames = displayJobs
            .mapValues { it.value.await() }
            .filterValues { !it.isNullOrBlank() }
            .mapValues { it.value!! }

        SrvDiscoveryResult(
            domain = domain,
            backupDomain = backupDomain,
            brokers = brokers.sorted(),
            exits = exits.sorted(),
            exitDisplayNames = exitDisplayNames,
            updatedAtEpochMs = System.currentTimeMillis(),
        )
    }

    suspend fun loadCache(): SrvDiscoveryResult? {
        val raw = store.data.first()[cacheKey] ?: return null
        return runCatching { json.decodeFromString<SrvDiscoveryResult>(raw) }.getOrNull()
    }

    private suspend fun saveCache(result: SrvDiscoveryResult) {
        val encoded = json.encodeToString(SrvDiscoveryResult.serializer(), result)
        store.edit { it[cacheKey] = encoded }
    }

    // MARK: - DoH

    private suspend fun lookupSafe(name: String): List<SrvRecord> =
        runCatching { lookup(name) }.getOrElse { emptyList() }

    private suspend fun lookup(name: String): List<SrvRecord> {
        var lastErr: Throwable = IllegalStateException("no providers")
        for (provider in DOH_PROVIDERS) {
            try {
                val records = queryDoh(provider, name)
                if (records.isNotEmpty()) return records
            } catch (e: Throwable) {
                Timber.tag(TAG).w(e, "DoH %s for %s failed", provider, name)
                lastErr = e
            }
        }
        throw lastErr
    }

    private suspend fun queryDoh(provider: String, name: String): List<SrvRecord> = withContext(Dispatchers.IO) {
        val url = "$provider?name=${name}&type=SRV"
        val request = Request.Builder()
            .url(url)
            .header("Accept", "application/dns-json")
            .build()
        client.newCall(request).execute().use { response ->
            require(response.isSuccessful) { "HTTP ${response.code}" }
            val body = response.body?.string().orEmpty()
            val parsed = json.decodeFromString<DohResponse>(body)
            require(parsed.status == 0) { "DoH status ${parsed.status}" }
            parsed.answer.orEmpty()
                .filter { it.type == 33 }
                .mapNotNull { ans ->
                    val parts = ans.data.split(" ")
                    if (parts.size != 4) return@mapNotNull null
                    val pri = parts[0].toIntOrNull() ?: return@mapNotNull null
                    val w = parts[1].toIntOrNull() ?: return@mapNotNull null
                    val p = parts[2].toIntOrNull() ?: return@mapNotNull null
                    val t = parts[3].trimEnd('.', ' ')
                    if (t.isEmpty()) null else SrvRecord(pri, w, p, t)
                }
        }
    }

    /**
     * TXT record lookup with provider failover, identical strategy to [lookup]
     * but for type 16. Returns the joined TXT data on the first non-empty
     * answer or `null` if every provider fails / record absent — TXT
     * metadata is always optional (display-only), so callers must tolerate
     * missing values.
     */
    private suspend fun lookupTxtSafe(name: String): String? {
        for (provider in DOH_PROVIDERS) {
            val txt = runCatching { queryDohTxt(provider, name) }
                .onFailure { Timber.tag(TAG).w(it, "DoH TXT %s for %s failed", provider, name) }
                .getOrNull()
            if (!txt.isNullOrBlank()) return txt
        }
        return null
    }

    private suspend fun queryDohTxt(provider: String, name: String): String? = withContext(Dispatchers.IO) {
        val url = "$provider?name=${name}&type=TXT"
        val request = Request.Builder()
            .url(url)
            .header("Accept", "application/dns-json")
            .build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return@withContext null
            val body = response.body?.string().orEmpty()
            val parsed = runCatching { json.decodeFromString<DohResponse>(body) }.getOrNull()
                ?: return@withContext null
            if (parsed.status != 0) return@withContext null
            // DoH JSON returns TXT data quoted: `"\"Toronto, Canada\""` (the
            // string literally includes the quote characters). Strip them.
            // Multiple character-strings in one TXT record come back
            // concatenated with adjacent quoted segments — `"\"a\" \"b\""`
            // — which we collapse into a single string by removing all `"`.
            parsed.answer.orEmpty()
                .firstOrNull { it.type == 16 }
                ?.data
                ?.replace("\"", "")
                ?.trim()
                ?.takeIf { it.isNotEmpty() }
        }
    }

    @Serializable
    private data class DohResponse(
        @SerialName("Status") val status: Int,
        @SerialName("Answer") val answer: List<DohAnswer>? = null,
    )

    @Serializable
    private data class DohAnswer(
        val name: String,
        val type: Int,
        val data: String,
    )

    private companion object {
        const val TAG = "vtx-srv"
        val DOH_PROVIDERS = listOf(
            "https://cloudflare-dns.com/dns-query",
            "https://dns.google/resolve",
        )
    }
}
