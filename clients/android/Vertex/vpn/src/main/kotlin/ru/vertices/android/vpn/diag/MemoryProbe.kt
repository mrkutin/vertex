package ru.vertices.android.vpn.diag

import android.app.ActivityManager
import android.content.Context
import android.os.Debug

/**
 * Process-level memory snapshot. iOS surfaces these via MetricKit; Android has
 * the equivalent in [Debug.MemoryInfo] (PSS / private dirty / shared dirty)
 * plus the [Runtime] heap counters.
 *
 * Sampled on demand from the Diagnostics screen — [Debug.getMemoryInfo] is
 * mildly expensive (walks the process maps) so don't poll it on a tight loop.
 */
data class MemorySnapshot(
    /** JVM heap currently in use, bytes. */
    val javaHeapUsedBytes: Long,
    /** JVM heap soft limit, bytes (per-app dalvik.vm.heapsize cap). */
    val javaHeapMaxBytes: Long,
    /** Native heap (mostly Skia, NDK libs), bytes. */
    val nativeHeapUsedBytes: Long,
    /** Total Proportional Set Size — best single-number RAM footprint, KB. */
    val totalPssKb: Int,
    /** Per-segment dirty pages (private dirty + shared dirty), KB. */
    val privateDirtyKb: Int,
    /** ActivityManager.MemoryInfo.lowMemory — system is under pressure. */
    val systemLowMemory: Boolean,
)

object MemoryProbe {
    fun snapshot(context: Context): MemorySnapshot {
        val rt = Runtime.getRuntime()
        val javaUsed = rt.totalMemory() - rt.freeMemory()
        val javaMax = rt.maxMemory()
        val nativeUsed = Debug.getNativeHeapAllocatedSize()
        val info = Debug.MemoryInfo().also { Debug.getMemoryInfo(it) }
        val sysInfo = ActivityManager.MemoryInfo()
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
        am?.getMemoryInfo(sysInfo)
        return MemorySnapshot(
            javaHeapUsedBytes = javaUsed,
            javaHeapMaxBytes = javaMax,
            nativeHeapUsedBytes = nativeUsed,
            totalPssKb = info.totalPss,
            privateDirtyKb = info.totalPrivateDirty + info.totalSharedDirty,
            systemLowMemory = sysInfo.lowMemory,
        )
    }
}
