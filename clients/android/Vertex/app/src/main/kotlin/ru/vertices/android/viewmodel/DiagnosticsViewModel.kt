package ru.vertices.android.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import ru.vertices.android.repository.DiagnosticsRepository
import ru.vertices.android.vpn.diag.BatterySnapshot
import ru.vertices.android.vpn.diag.MemorySnapshot
import kotlinx.coroutines.delay

data class DiagnosticsUi(
    val memory: MemorySnapshot? = null,
    val battery: BatterySnapshot? = null,
    val logTail: String = "",
)

@HiltViewModel
class DiagnosticsViewModel @Inject constructor(
    private val diag: DiagnosticsRepository,
) : ViewModel() {

    /**
     * Sample memory + battery + log tail every 3 seconds. Cheap enough to
     * keep the screen alive on; heavy enough that we don't poll on every
     * recomposition. The Flow is hot-only-while-subscribed so backgrounding
     * the screen stops the timer.
     */
    private val sampler = flow {
        while (true) {
            emit(refresh())
            delay(SAMPLE_INTERVAL_MS)
        }
    }

    val state: StateFlow<DiagnosticsUi> = sampler
        .stateIn(viewModelScope, SharingStarted.WhileSubscribed(2_000), DiagnosticsUi())

    /** "Sharing zip…" / "Ready" / "Failed". One-shot via [_share]. */
    sealed interface ShareState {
        data object Idle : ShareState
        data object Building : ShareState
        data class Ready(val uri: android.net.Uri) : ShareState
        data class Failed(val message: String) : ShareState
    }

    private val _share = MutableStateFlow<ShareState>(ShareState.Idle)
    val share: StateFlow<ShareState> = _share.asStateFlow()

    fun exportZip() {
        if (_share.value is ShareState.Building) return
        _share.value = ShareState.Building
        viewModelScope.launch {
            val uri = withContext(Dispatchers.IO) { diag.exportZip() }
            _share.value = if (uri != null) ShareState.Ready(uri)
            else ShareState.Failed("Could not build diagnostics zip")
        }
    }

    fun consumeShare() {
        // Called by the screen after the share Intent is dispatched (or the
        // user dismissed the failure banner) so the next button tap can
        // produce a fresh URI.
        _share.value = ShareState.Idle
    }

    /**
     * Surface a failure that originated outside the repository — currently
     * only `ActivityNotFoundException` from `startActivity(chooser)` on
     * stripped-down devices that don't have an app accepting ACTION_SEND
     * with application/zip. Without this the user taps Share and nothing
     * visible happens.
     */
    fun shareFailed(message: String) {
        _share.value = ShareState.Failed(message)
    }

    private suspend fun refresh(): DiagnosticsUi = withContext(Dispatchers.IO) {
        DiagnosticsUi(
            memory = diag.memorySnapshot(),
            battery = diag.batterySnapshot(),
            logTail = diag.logTail(),
        )
    }

    private companion object {
        const val SAMPLE_INTERVAL_MS = 3_000L
    }
}
