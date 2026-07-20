package ru.vertices.android.app

import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import dagger.hilt.android.AndroidEntryPoint
import ru.vertices.android.ui.navigation.VertexNavGraph
import ru.vertices.android.ui.sheets.PermissionDeniedScreen
import ru.vertices.android.ui.theme.VertexTheme
import timber.log.Timber

@AndroidEntryPoint
class MainActivity : ComponentActivity() {

    private val vpnPermissionGranted = mutableStateOf(false)
    /**
     * `true` when the user has explicitly cancelled the system VPN-permission
     * dialog. We use this to decide whether to show the full-screen
     * "Permission required" overlay (mirror of iOS PermissionDeniedView).
     */
    private val vpnPermissionDenied = mutableStateOf(false)

    private val vpnPermLauncher = registerForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { result ->
        val ok = result.resultCode == RESULT_OK
        vpnPermissionGranted.value = ok
        vpnPermissionDenied.value = !ok
        if (!ok) Timber.tag(TAG).w("VPN permission denied")
    }

    private val notifPermLauncher = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { granted ->
        if (!granted) Timber.tag(TAG).w("POST_NOTIFICATIONS denied")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // Restore the "user explicitly cancelled the VPN dialog" flag across
        // configuration changes — without this, a rotation right after the
        // user dismisses the system permission prompt drops the
        // PermissionDeniedScreen and the app silently snaps back to the
        // Connect screen with no explanation. The "granted" side is always
        // re-derived from VpnService.prepare so it stays accurate after
        // changes from the system Settings screen.
        vpnPermissionGranted.value = (VpnService.prepare(this) == null)
        vpnPermissionDenied.value = savedInstanceState?.getBoolean(KEY_PERMISSION_DENIED) == true

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            val perm = android.Manifest.permission.POST_NOTIFICATIONS
            if (checkSelfPermission(perm) != PackageManager.PERMISSION_GRANTED) {
                notifPermLauncher.launch(perm)
            }
        }

        setContent {
            VertexTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    val granted = remember { vpnPermissionGranted }
                    val denied by remember { vpnPermissionDenied }
                    VertexNavGraph(onRequestVpnPermission = {
                        if (granted.value) {
                            true
                        } else {
                            requestVpnPermission()
                            false
                        }
                    })
                    if (denied) {
                        PermissionDeniedScreen(
                            onTryAgain = {
                                vpnPermissionDenied.value = false
                                requestVpnPermission()
                            },
                            onCancel = { vpnPermissionDenied.value = false },
                        )
                    }
                }
            }
        }
    }

    private fun requestVpnPermission() {
        val prepare: Intent? = VpnService.prepare(this)
        if (prepare == null) {
            vpnPermissionGranted.value = true
            vpnPermissionDenied.value = false
        } else {
            vpnPermLauncher.launch(prepare)
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putBoolean(KEY_PERMISSION_DENIED, vpnPermissionDenied.value)
    }

    companion object {
        private const val TAG = "vtx-main"
        private const val KEY_PERMISSION_DENIED = "vtx.vpnPermissionDenied"
    }
}
