package ru.vertices.android.app

import android.app.Application
import dagger.hilt.android.HiltAndroidApp
import ru.vertices.android.BuildConfig
import ru.vertices.android.vpn.diag.FileLogger
import timber.log.Timber

@HiltAndroidApp
class VertexApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        if (BuildConfig.DEBUG) {
            Timber.plant(Timber.DebugTree())
        }
        // File logger plants in every build — this is the artifact behind
        // the Diagnostics screen's "Export" button. Without it a release
        // build sends nothing useful when a user reports an issue, since
        // logcat is privileged on production hardware.
        Timber.plant(FileLogger.create(this))
    }
}
