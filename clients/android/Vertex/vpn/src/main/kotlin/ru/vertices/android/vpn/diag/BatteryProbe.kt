package ru.vertices.android.vpn.diag

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager

/**
 * Battery state read via the sticky [Intent.ACTION_BATTERY_CHANGED] broadcast.
 * Mirrors the iOS Diagnostics view so a user reporting "VPN drains battery"
 * can paste the actual percentage and charging state we observed at sample
 * time alongside their crash report.
 */
data class BatterySnapshot(
    /** 0..100 (percent). [-1] when the system can't report (rare; e.g. emulator). */
    val levelPercent: Int,
    /** True while the device is charging or full. */
    val charging: Boolean,
    /** AC / USB / Wireless / None. */
    val plugged: PluggedState,
) {
    enum class PluggedState { NONE, AC, USB, WIRELESS, OTHER }
}

object BatteryProbe {
    fun snapshot(context: Context): BatterySnapshot {
        // Battery state lives in a sticky broadcast — passing null receiver
        // returns the last [Intent] without registering for future updates,
        // which is exactly what we want for an on-demand read.
        val intent: Intent? = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        if (intent == null) return BatterySnapshot(-1, false, BatterySnapshot.PluggedState.NONE)

        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        val pct = if (level >= 0 && scale > 0) (level * 100 / scale) else -1
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
            status == BatteryManager.BATTERY_STATUS_FULL
        val plugged = when (intent.getIntExtra(BatteryManager.EXTRA_PLUGGED, -1)) {
            0 -> BatterySnapshot.PluggedState.NONE
            BatteryManager.BATTERY_PLUGGED_AC -> BatterySnapshot.PluggedState.AC
            BatteryManager.BATTERY_PLUGGED_USB -> BatterySnapshot.PluggedState.USB
            BatteryManager.BATTERY_PLUGGED_WIRELESS -> BatterySnapshot.PluggedState.WIRELESS
            else -> BatterySnapshot.PluggedState.OTHER
        }
        return BatterySnapshot(pct, charging, plugged)
    }
}
