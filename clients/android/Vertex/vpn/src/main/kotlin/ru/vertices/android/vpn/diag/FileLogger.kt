package ru.vertices.android.vpn.diag

import android.content.Context
import android.util.Log
import timber.log.Timber
import java.io.File
import java.io.FileOutputStream
import java.io.PrintWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

/**
 * Timber tree that writes every log line to a rolling file in
 * `filesDir/logs/`. The MQTT/TUN engine and the UI both go through Timber, so
 * planting this once in [VertexApplication.onCreate] captures the whole app's
 * trace without per-call sites changing.
 *
 * Layout:
 *   logs/vtx-current.log      ← active file, written to
 *   logs/vtx-1.log .. vtx-N.log ← rotated, oldest gets dropped
 *
 * Rotation is size-based (≈1 MB per file). Total budget = 7 × 1 MB ≈ 7 MB; on
 * an iOS-comparable Diagnostics export this is plenty of history without
 * eating user storage.
 *
 * Thread model:
 *  - All writes run on a single dedicated executor so concurrent log calls
 *    from MQTT receiver / TUN reader / UI never interleave a partial line.
 *  - The Timber call itself is non-blocking — we hand the formatted line to
 *    the executor and return — so logging on the packet path doesn't slow
 *    the tunnel.
 *
 * Stack-trace handling: when [Throwable] is non-null we follow Timber's
 * convention and include the stack-trace text below the message, matching
 * what `adb logcat` would show. That keeps the file format greppable with
 * the same rules as logcat output.
 */
class FileLogger private constructor(
    private val logsDir: File,
) : Timber.Tree() {

    /**
     * Single-thread bounded executor. The default `Executors.newSingleThreadExecutor`
     * uses an unbounded `LinkedBlockingQueue`; under sustained Timber pressure
     * from the packet pipeline (e.g. broker reconnect storm with stack-trace
     * exceptions, ~hundreds of lines/sec) and a stalled flash, formatted
     * lines + their captured throwables would pile up in heap until the
     * process gets killed by the kernel.
     *
     * Cap the queue at 4096 entries (≈8 MB if every line is a 2 KB stack
     * trace, fits well under the per-process limit). [DiscardOldestPolicy]
     * means we drop the oldest pending line in favour of the newest — a
     * tunnel flap is more interesting than a connect from 30 s ago.
     */
    private val executor = ThreadPoolExecutor(
        /* corePoolSize     = */ 1,
        /* maximumPoolSize  = */ 1,
        /* keepAliveTime    = */ 0L,
        /* unit             = */ TimeUnit.MILLISECONDS,
        /* workQueue        = */ ArrayBlockingQueue(QUEUE_CAPACITY),
        /* threadFactory    = */ { r -> Thread(r, "vtx-filelog").apply { isDaemon = true } },
        /* handler          = */ ThreadPoolExecutor.DiscardOldestPolicy(),
    )
    private val tsFormat = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)

    override fun log(priority: Int, tag: String?, message: String, t: Throwable?) {
        // Snapshot the inputs on the caller thread — `tag` is read off a
        // ThreadLocal Timber stash that resets after the call returns, so
        // capturing here keeps the executor task self-contained.
        val ts = tsFormat.format(Date())
        val pri = priorityChar(priority)
        val tagStr = tag ?: "?"
        val full = if (t == null) message
        else buildString {
            append(message)
            append('\n')
            append(Log.getStackTraceString(t))
        }
        executor.execute { writeLine(ts, pri, tagStr, full) }
    }

    private fun writeLine(ts: String, priority: Char, tag: String, message: String) {
        try {
            ensureLogsDir()
            val current = File(logsDir, CURRENT_NAME)
            if (current.exists() && current.length() >= MAX_FILE_BYTES) {
                rotate()
            }
            FileOutputStream(current, /* append = */ true).use { fos ->
                PrintWriter(fos).use { pw ->
                    // "MM-dd HH:mm:ss.SSS I tag: message"
                    pw.append(ts).append(' ').append(priority).append(' ').append(tag).append(": ")
                    pw.println(message)
                    pw.flush()
                }
            }
        } catch (t: Throwable) {
            // Last-ditch — print to logcat if our own writer broke. Don't
            // recurse into Timber here; it'd loop straight back in.
            Log.w(SELF_TAG, "FileLogger write failed", t)
        }
    }

    private fun rotate() {
        val current = File(logsDir, CURRENT_NAME)
        val last = File(logsDir, archiveName(MAX_BACKUPS))
        if (last.exists() && !last.delete()) {
            // Use logcat directly — recursing into Timber here would re-enter
            // this same executor, deadlocking the writer.
            Log.w(SELF_TAG, "rotate: failed to delete oldest archive ${last.name}")
        }
        for (i in MAX_BACKUPS - 1 downTo 1) {
            val src = File(logsDir, archiveName(i))
            if (src.exists()) {
                val dst = File(logsDir, archiveName(i + 1))
                if (!src.renameTo(dst)) {
                    Log.w(SELF_TAG, "rotate: rename ${src.name} → ${dst.name} failed")
                }
            }
        }
        if (!current.renameTo(File(logsDir, archiveName(1)))) {
            Log.w(SELF_TAG, "rotate: rename ${current.name} → ${archiveName(1)} failed")
        }
        // Drop any orphan slots above MAX_BACKUPS that may have accumulated
        // from a prior version with a higher cap, or from a partial rename.
        // Cheap and bounds total disk usage at MAX_BACKUPS+1 files
        // regardless of rename failures.
        logsDir.listFiles()?.forEach { f ->
            val name = f.name
            if (!name.startsWith("vtx-")) return@forEach
            if (name == CURRENT_NAME) return@forEach
            val idx = name.removePrefix("vtx-").removeSuffix(".log").toIntOrNull() ?: return@forEach
            if (idx < 1 || idx > MAX_BACKUPS) f.delete()
        }
    }

    private fun ensureLogsDir() {
        if (!logsDir.exists()) logsDir.mkdirs()
    }

    private fun archiveName(idx: Int): String = "vtx-$idx.log"

    private fun priorityChar(priority: Int): Char = when (priority) {
        Log.VERBOSE -> 'V'
        Log.DEBUG -> 'D'
        Log.INFO -> 'I'
        Log.WARN -> 'W'
        Log.ERROR -> 'E'
        Log.ASSERT -> 'A'
        else -> '?'
    }

    companion object {
        private const val SELF_TAG = "vtx-filelog"
        private const val CURRENT_NAME = "vtx-current.log"
        // 1 MiB per file × 7 files ≈ 7 MiB total. Single-line writes that
        // exceed the threshold (e.g. a 4-8 KB stack trace) overshoot by one
        // line before rolling — bound the worst case at +1 message/slot,
        // which is acceptable.
        private const val MAX_FILE_BYTES: Long = 1L shl 20
        private const val MAX_BACKUPS: Int = 6 // current + 6 archives = 7 total
        /**
         * Bounded queue capacity for the writer executor. ~4096 entries
         * × ~2 KB worst-case line ≈ 8 MB heap ceiling for the backlog.
         */
        private const val QUEUE_CAPACITY: Int = 4096

        /**
         * Resolve the logs directory used by [FileLogger] under any context.
         * Exposed so [DiagnosticsRepository] can list/read/zip the files
         * without owning the same path constant in two places.
         */
        fun logsDir(context: Context): File = File(context.filesDir, "logs")

        fun create(context: Context): FileLogger = FileLogger(logsDir(context.applicationContext))
    }
}
