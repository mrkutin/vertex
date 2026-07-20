package ru.vertices.android.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import ru.vertices.android.core.identity.IdentityKeyStore
import ru.vertices.android.repository.PasswordRepository
import ru.vertices.android.repository.RuNetsRepository
import ru.vertices.android.repository.SettingsRepository

data class SettingsUi(
    val domain: String = SettingsRepository.DEFAULT_DOMAIN,
    val clientName: String = SettingsRepository.DEFAULT_NAME,
    val selectedExit: String = SettingsRepository.DEFAULT_EXIT,
    val selectedBroker: String = "",
    val splitTunnel: Boolean = false,
    val password: String = "",
)

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val settings: SettingsRepository,
    private val passwords: PasswordRepository,
    private val identityStore: IdentityKeyStore,
    private val ruNets: RuNetsRepository,
) : ViewModel() {

    /**
     * Single source of truth lives in [PasswordRepository.flow] — this VM
     * just re-exposes it so the screen can observe edits made elsewhere
     * (e.g. a future import-config flow) without going stale. Triggering a
     * [PasswordRepository.get] up front primes the StateFlow with the
     * persisted value before any subscriber attaches.
     */
    init { passwords.get() }
    val password: StateFlow<String> = passwords.flow

    private data class CoreSettings(val domain: String, val name: String, val broker: String)
    private data class ExitAndExtras(val exit: String, val split: Boolean, val pass: String)

    val ui: StateFlow<SettingsUi> = combine(
        combine(settings.domain, settings.clientName, settings.selectedBroker, ::CoreSettings),
        combine(settings.selectedExit, settings.splitTunnelEnabled, passwords.flow, ::ExitAndExtras),
    ) { core, extras ->
        SettingsUi(
            domain = core.domain,
            clientName = core.name,
            selectedBroker = core.broker,
            selectedExit = extras.exit,
            splitTunnel = extras.split,
            password = extras.pass,
        )
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), SettingsUi())

    fun setDomain(value: String) { viewModelScope.launch { settings.setDomain(value) } }
    fun setClientName(name: String) { viewModelScope.launch { settings.setClientName(name) } }
    fun setExit(exit: String) { viewModelScope.launch { settings.setSelectedExit(exit) } }
    fun setSplitTunnel(value: Boolean) { viewModelScope.launch { settings.setSplitTunnelEnabled(value) } }
    fun setPassword(p: String) {
        // Repository.set updates its own StateFlow, so observers of [password]
        // (and any other ViewModel that subscribes to PasswordRepository.flow)
        // pick up the change without further plumbing.
        passwords.set(p)
    }

    val identityPubkeyHex: String get() = identityStore.loadOrCreate().publicKeyHex

    fun resetIdentity() {
        identityStore.reset()
    }

    // ---------------- RU CIDR list (split tunnel) ----------------

    val ruNetsInfo: StateFlow<RuNetsRepository.Info> = ruNets.info
    val ruNetsRefresh: StateFlow<RuNetsRepository.RefreshState> = ruNets.refreshState

    /**
     * Triggering goes through the repository's own application-lived scope, so
     * leaving Settings while a refresh is in-flight doesn't cancel the
     * download and leave a `.tmp` file behind in `filesDir`.
     */
    fun refreshRuNets() {
        ruNets.triggerRefresh()
    }
}
