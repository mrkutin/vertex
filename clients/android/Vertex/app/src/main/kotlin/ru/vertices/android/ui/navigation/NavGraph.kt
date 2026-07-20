package ru.vertices.android.ui.navigation

import androidx.compose.runtime.Composable
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import ru.vertices.android.ui.connect.ConnectScreen
import ru.vertices.android.ui.pickers.BrokerListScreen
import ru.vertices.android.ui.pickers.ExitListScreen
import ru.vertices.android.ui.settings.AboutScreen
import ru.vertices.android.ui.settings.DiagnosticsScreen
import ru.vertices.android.ui.settings.IdentityKeyScreen
import ru.vertices.android.ui.settings.SettingsScreen

object Routes {
    const val CONNECT = "connect"
    const val BROKER_PICKER = "brokers"
    const val EXIT_PICKER = "exits"
    const val SETTINGS = "settings"
    const val IDENTITY = "identity"
    const val DIAGNOSTICS = "diagnostics"
    const val ABOUT = "about"
}

@Composable
fun VertexNavGraph(onRequestVpnPermission: () -> Boolean) {
    val nav = rememberNavController()
    NavHost(navController = nav, startDestination = Routes.CONNECT) {
        composable(Routes.CONNECT) {
            ConnectScreen(
                onSettingsClick = { nav.navigate(Routes.SETTINGS) },
                onRequestVpnPermission = onRequestVpnPermission,
                onBrokerPickerClick = { nav.navigate(Routes.BROKER_PICKER) },
                onExitPickerClick = { nav.navigate(Routes.EXIT_PICKER) },
            )
        }
        composable(Routes.BROKER_PICKER) {
            BrokerListScreen(onBack = { nav.popBackStack() })
        }
        composable(Routes.EXIT_PICKER) {
            ExitListScreen(onBack = { nav.popBackStack() })
        }
        composable(Routes.SETTINGS) {
            SettingsScreen(
                onBack = { nav.popBackStack() },
                onIdentityClick = { nav.navigate(Routes.IDENTITY) },
                onDiagnosticsClick = { nav.navigate(Routes.DIAGNOSTICS) },
                onAboutClick = { nav.navigate(Routes.ABOUT) },
            )
        }
        composable(Routes.IDENTITY) {
            IdentityKeyScreen(onBack = { nav.popBackStack() })
        }
        composable(Routes.DIAGNOSTICS) {
            DiagnosticsScreen(onBack = { nav.popBackStack() })
        }
        composable(Routes.ABOUT) {
            AboutScreen(onBack = { nav.popBackStack() })
        }
    }
}
