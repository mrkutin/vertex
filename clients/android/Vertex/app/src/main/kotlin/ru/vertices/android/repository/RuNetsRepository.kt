package ru.vertices.android.repository

import android.content.Context
import android.system.ErrnoException
import android.system.Os
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import timber.log.Timber
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.concurrent.TimeUnit
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.coroutines.cancellation.CancellationException

/**
 * Manages the local copy of the RU CIDR aggregated zone used by the split-tunnel
 * feature. Keeps two states:
 *
 *  - [Source.BUNDLED]: no override on disk — the loader falls back to the APK
 *    asset shipped with the build. This is the cold-start state of every new
 *    install and persists until the user (or a future scheduler) refreshes.
 *  - [Source.UPDATED]: a refreshed copy lives in `filesDir/ru-aggregated.zone`,
 *    with a real mtime and size. [RUNetsLoader] in `:vpn` reads from this path
 *    first so refreshes take effect on the next connect without rebuilding the
 *    app.
 *
 * The refresh URL mirrors the iOS reference. ipdeny.com publishes one
 * aggregated zone per country; the file is plain text, ~150 KB, line-per-CIDR.
 *
 * Concurrency model:
 *  - [refresh] runs in [scope] (application-lived), not the caller's scope, so
 *    a refresh started from Settings survives the user backing out before it
 *    finishes.
 *  - [mu] serializes refreshes against each other.
 *  - The VPN's `RUNetsLoader.load()` reads the zone file in this same process
 *    on the connect path; we use POSIX `Os.rename` for the swap so an in-flight
 *    reader keeps its open FD on the old inode and never sees a half-written
 *    file. The previous `renameTo` + copy-and-delete fallback was unsafe — the
 *    fallback could truncate-then-rewrite the live target.
 */
@Singleton
class RuNetsRepository @Inject constructor(
    @ApplicationContext private val context: Context,
    private val httpClient: OkHttpClient,
) {

    enum class Source { BUNDLED, UPDATED }

    data class Info(
        val lineCount: Int,
        val sizeBytes: Long,
        /** File mtime when [source] is [Source.UPDATED]; `null` for [Source.BUNDLED]. */
        val updatedAtEpochMs: Long?,
        val source: Source,
    )

    sealed interface RefreshState {
        data object Idle : RefreshState
        data object InProgress : RefreshState
        data object Succeeded : RefreshState
        data class Failed(val message: String) : RefreshState
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mu = Mutex()
    private val _info = MutableStateFlow(Info(0, 0, null, Source.BUNDLED))
    val info: StateFlow<Info> = _info.asStateFlow()
    private val _refreshState = MutableStateFlow<RefreshState>(RefreshState.Idle)
    val refreshState: StateFlow<RefreshState> = _refreshState.asStateFlow()

    init {
        // Avoid blocking the calling thread (typically the main thread when
        // Hilt instantiates a @Singleton on the first @Inject) for a ~150 KB
        // line-count walk.
        scope.launch { _info.value = loadInfoBlocking() }
    }

    /**
     * Fire-and-forget refresh trigger. Runs in the application-scoped [scope]
     * so a navigation away from the Settings screen mid-download doesn't
     * cancel the work and leak a temp file. Observe progress via
     * [refreshState]; the new [info] arrives through [info] on success.
     *
     * Concurrent calls are coalesced — a second tap while a refresh is
     * already running is a no-op.
     */
    fun triggerRefresh() {
        if (_refreshState.value == RefreshState.InProgress) return
        scope.launch {
            _refreshState.value = RefreshState.InProgress
            val result = refresh()
            _refreshState.value = result.fold(
                onSuccess = { RefreshState.Succeeded },
                onFailure = { RefreshState.Failed(it.message ?: "refresh failed") },
            )
        }
    }

    /**
     * Download the latest RU aggregated zone from ipdeny.com and atomically
     * replace `filesDir/ru-aggregated.zone`. Returns the new [Info] on success
     * or the throwable from the network/IO layer on failure.
     *
     * Direct callers must own the lifecycle they want — [triggerRefresh] is
     * the supported entry point for UI surfaces that come and go.
     */
    suspend fun refresh(): Result<Info> = mu.withLock {
        withContext(Dispatchers.IO) {
            val tmp = File(context.filesDir, "$FILE_NAME.tmp")
            val target = File(context.filesDir, FILE_NAME)
            try {
                val req = Request.Builder()
                    .url(URL)
                    .header("User-Agent", "Vertex-Android/${USER_AGENT_VERSION}")
                    .header("Accept", "text/plain")
                    .build()
                httpClient.newCall(req).execute().use { res ->
                    if (!res.isSuccessful) {
                        return@withContext Result.failure(IOException("HTTP ${res.code}"))
                    }
                    val body = res.body ?: return@withContext Result.failure(IOException("empty body"))
                    body.byteStream().use { input ->
                        FileOutputStream(tmp).use { out -> input.copyTo(out) }
                    }
                }
                // Sanity-check the body before promoting it. The bundled zone
                // is ~135 KB / 8585 lines; a captive-portal HTML page or a
                // truncated download tends to be far smaller and lacks the
                // dotted-quad+slash shape. Lower bounds chosen to leave room
                // for ipdeny shrinking the list (allocation withdrawals) but
                // still catch garbage.
                val size = tmp.length()
                if (size < MIN_BODY_BYTES) {
                    return@withContext Result.failure(IOException("payload too small: $size bytes"))
                }
                val parsed = countCidrLinesValid(tmp)
                if (parsed < MIN_CIDR_LINES) {
                    return@withContext Result.failure(IOException("payload has only $parsed CIDRs (< $MIN_CIDR_LINES)"))
                }
                // Atomic swap. POSIX rename is required by spec to be atomic
                // on the same filesystem; no fallback — if it fails, leave
                // the existing target untouched and report the failure.
                try {
                    Os.rename(tmp.absolutePath, target.absolutePath)
                } catch (e: ErrnoException) {
                    return@withContext Result.failure(IOException("rename failed: ${e.message}", e))
                }
                val info = loadInfoBlocking()
                _info.value = info
                Timber.tag(TAG).i(
                    "refreshed: %d lines, %d bytes, mtime=%d",
                    info.lineCount, info.sizeBytes, info.updatedAtEpochMs ?: -1L,
                )
                Result.success(info)
            } catch (t: Throwable) {
                if (t is CancellationException) throw t
                Timber.tag(TAG).w(t, "refresh failed")
                Result.failure(t)
            } finally {
                runCatching { if (tmp.exists()) tmp.delete() }
            }
        }
    }

    private fun loadInfoBlocking(): Info {
        val file = File(context.filesDir, FILE_NAME)
        if (file.exists()) {
            val count = countLines { file.inputStream() }
            return Info(
                lineCount = count,
                sizeBytes = file.length(),
                updatedAtEpochMs = file.lastModified(),
                source = Source.UPDATED,
            )
        }
        return runCatching {
            val count = countLines { context.assets.open(FILE_NAME) }
            // Asset size isn't accessible without a full read; report -1 so
            // the UI can render "—" rather than a misleading 0.
            Info(count, sizeBytes = -1, updatedAtEpochMs = null, source = Source.BUNDLED)
        }.getOrElse {
            Timber.tag(TAG).w(it, "asset $FILE_NAME unreadable")
            Info(0, 0, null, Source.BUNDLED)
        }
    }

    private inline fun countLines(open: () -> java.io.InputStream): Int {
        var n = 0
        open().bufferedReader().use { r ->
            r.lineSequence().forEach { line ->
                if (line.isNotBlank() && !line.startsWith("#")) n++
            }
        }
        return n
    }

    /** Stricter than [countLines]: only counts lines that actually parse as `dotted-quad/prefix`. */
    private fun countCidrLinesValid(file: File): Int {
        var n = 0
        file.bufferedReader().use { r ->
            r.lineSequence().forEach { raw ->
                val line = raw.trim()
                if (line.isEmpty() || line.startsWith("#")) return@forEach
                val slash = line.indexOf('/')
                if (slash <= 0) return@forEach
                val host = line.substring(0, slash)
                val prefix = line.substring(slash + 1).toIntOrNull() ?: return@forEach
                if (prefix !in 0..32) return@forEach
                val parts = host.split('.')
                if (parts.size != 4) return@forEach
                if (parts.any { (it.toIntOrNull() ?: -1) !in 0..255 }) return@forEach
                n++
            }
        }
        return n
    }

    companion object {
        private const val TAG = "vtx-runets-repo"
        private const val FILE_NAME = "ru-aggregated.zone"
        private const val URL = "https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone"
        private const val USER_AGENT_VERSION = "1.3"

        // Bundled zone is ~135 KB / 8585 lines. A truncated download or a
        // captive-portal HTML page tends to be far below either threshold;
        // these floors leave room for ipdeny shrinking the list while still
        // refusing obvious garbage.
        private const val MIN_BODY_BYTES: Long = 50_000L
        private const val MIN_CIDR_LINES: Int = 1_000
    }
}
