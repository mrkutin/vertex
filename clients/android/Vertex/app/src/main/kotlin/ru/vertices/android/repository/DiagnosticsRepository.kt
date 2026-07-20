package ru.vertices.android.repository

import android.content.Context
import androidx.core.content.FileProvider
import dagger.hilt.android.qualifiers.ApplicationContext
import ru.vertices.android.BuildConfig
import ru.vertices.android.core.identity.IdentityKeyStore
import ru.vertices.android.vpn.diag.BatteryProbe
import ru.vertices.android.vpn.diag.BatterySnapshot
import ru.vertices.android.vpn.diag.FileLogger
import ru.vertices.android.vpn.diag.MemoryProbe
import ru.vertices.android.vpn.diag.MemorySnapshot
import java.io.File
import java.io.FileOutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Aggregates the bits the Diagnostics screen needs: the live memory and
 * battery snapshots, a tail of the rolling log, and a "share this with
 * support" zip exporter that bundles logs + metadata into a single file
 * surfaced via [FileProvider].
 *
 * The exported zip mirrors what the iOS DiagnosticsView "Export" button
 * produces, so support can ask for the same artifact regardless of platform:
 *   - `summary.txt`  — app version, identity pubkey, last metrics snapshot
 *   - `logs/vtx-*.log` — current rolling log + archives
 *
 * No PII beyond what the user has already typed in (broker passwords stay
 * in the encrypted prefs and are NOT included). Identity public key is fine
 * to share — that's how the exit identifies them anyway.
 */
@Singleton
class DiagnosticsRepository @Inject constructor(
    @ApplicationContext private val context: Context,
    private val identityStore: IdentityKeyStore,
    private val settings: SettingsRepository,
) {

    fun memorySnapshot(): MemorySnapshot = MemoryProbe.snapshot(context)
    fun batterySnapshot(): BatterySnapshot = BatteryProbe.snapshot(context)

    /** Pubkey is fine to share — broker uses it to identify the device. */
    private fun identityPubkeyOrNull(): String? = runCatching {
        identityStore.loadOrCreate().publicKeyHex
    }.getOrNull()

    /**
     * Tail the active log file. Streaming a small window from the end of the
     * file keeps the UI render path bounded — the full 7 MB never materialises
     * in memory, only the last [maxBytes] do.
     */
    fun logTail(maxBytes: Int = DEFAULT_LOG_TAIL_BYTES): String {
        val file = File(FileLogger.logsDir(context), "vtx-current.log")
        if (!file.exists() || file.length() == 0L) return ""
        val len = file.length()
        val window = minOf(len, maxBytes.toLong())
        val skip = len - window
        return runCatching {
            file.inputStream().use { stream ->
                if (skip > 0) stream.skip(skip)
                stream.readBytes().toString(Charsets.UTF_8)
                    // Drop a possibly-truncated leading line so the tail
                    // always starts at a clean log entry.
                    .substringAfter('\n', missingDelimiterValue = "")
            }
        }.getOrElse { "" }
    }

    /**
     * Build a zip in `cacheDir/diagnostics/` and return a content:// URI that
     * can be passed to `Intent.ACTION_SEND` via [FileProvider]. Older zips
     * are cleared on each export so the cache doesn't grow.
     *
     * Suspend so [buildSummary] can await `settings.snapshot()` directly
     * instead of using `runBlocking`. Caller is expected to launch on
     * `Dispatchers.IO` — the zip stream itself is blocking, plain Java I/O.
     */
    suspend fun exportZip(): android.net.Uri? {
        val outDir = File(context.cacheDir, "diagnostics").apply {
            mkdirs()
            // Best-effort cleanup — keep the most-recent attempt around for
            // quick re-share if the user cancels the share-sheet, drop older.
            listFiles()?.sortedByDescending { it.lastModified() }?.drop(1)?.forEach { it.delete() }
        }
        val ts = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date())
        val target = File(outDir, "vertex-diagnostics-$ts.zip")
        val summary = buildSummary()
        try {
            ZipOutputStream(FileOutputStream(target)).use { zip ->
                zip.putNextEntry(ZipEntry("summary.txt"))
                zip.write(summary.toByteArray(Charsets.UTF_8))
                zip.closeEntry()

                val logsDir = FileLogger.logsDir(context)
                val files = logsDir.listFiles()?.toList().orEmpty()
                for (f in files.sortedBy { it.name }) {
                    zip.putNextEntry(ZipEntry("logs/${f.name}"))
                    f.inputStream().use { it.copyTo(zip) }
                    zip.closeEntry()
                }
            }
        } catch (t: Throwable) {
            return null
        }
        return runCatching {
            // Authority must match the <provider> declared in the manifest.
            // Built from applicationId so debug (.debug suffix) and release
            // builds end up with distinct authorities and never collide if
            // both are installed side-by-side.
            val authority = "${BuildConfig.APPLICATION_ID}.fileprovider"
            FileProvider.getUriForFile(context, authority, target)
        }.getOrNull()
    }

    private suspend fun buildSummary(): String {
        val pub = identityPubkeyOrNull()
        val snap = runCatching { settings.snapshot() }.getOrNull()
        val mem = memorySnapshot()
        val bat = batterySnapshot()
        return buildString {
            appendLine("Vertex Android — Diagnostics")
            appendLine("Generated: ${Date()}")
            appendLine()
            appendLine("App")
            appendLine("  versionName=${BuildConfig.VERSION_NAME ?: "?"}")
            appendLine("  versionCode=${BuildConfig.VERSION_CODE}")
            appendLine("  applicationId=${BuildConfig.APPLICATION_ID}")
            appendLine("  build=${if (BuildConfig.DEBUG) "debug" else "release"}")
            appendLine()
            appendLine("Device")
            appendLine("  manufacturer=${android.os.Build.MANUFACTURER}")
            appendLine("  model=${android.os.Build.MODEL}")
            // Build.PRODUCT can encode carrier/region (e.g. X1_II_RU on some
            // Sony/Xiaomi SKUs). Build.DEVICE is the codename ("pdx_206")
            // which is anonymous enough for support without de-identifying
            // a user who pastes summary.txt into a public tracker.
            appendLine("  device=${android.os.Build.DEVICE}")
            appendLine("  android=${android.os.Build.VERSION.RELEASE} (sdk=${android.os.Build.VERSION.SDK_INT})")
            appendLine()
            appendLine("Identity")
            appendLine("  pubkey=${pub ?: "<unavailable>"}")
            appendLine()
            appendLine("Settings (no secrets)")
            if (snap == null) {
                appendLine("  <unavailable>")
            } else {
                appendLine("  domain=${snap.domain}")
                appendLine("  clientName=${snap.clientName}")
                appendLine("  selectedBroker=${snap.selectedBroker}")
                appendLine("  selectedExit=${snap.selectedExit}")
                appendLine("  splitTunnel=${snap.splitTunnel}")
            }
            appendLine()
            appendLine("Memory")
            appendLine("  javaHeapUsed=${mem.javaHeapUsedBytes} bytes")
            appendLine("  javaHeapMax=${mem.javaHeapMaxBytes} bytes")
            appendLine("  nativeHeapUsed=${mem.nativeHeapUsedBytes} bytes")
            appendLine("  totalPss=${mem.totalPssKb} KB")
            appendLine("  privateDirty=${mem.privateDirtyKb} KB")
            appendLine("  systemLowMemory=${mem.systemLowMemory}")
            appendLine()
            appendLine("Battery")
            appendLine("  levelPercent=${bat.levelPercent}")
            appendLine("  charging=${bat.charging}")
            appendLine("  plugged=${bat.plugged}")
        }
    }

    companion object {
        private const val DEFAULT_LOG_TAIL_BYTES: Int = 16 * 1024
    }
}
