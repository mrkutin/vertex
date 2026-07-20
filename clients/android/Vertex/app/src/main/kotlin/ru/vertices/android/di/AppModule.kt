package ru.vertices.android.di

import android.content.Context
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import okhttp3.OkHttpClient
import ru.vertices.android.core.identity.IdentityKeyStore
import ru.vertices.android.vpn.identity.KeystoreIdentityKeyStore
import java.util.concurrent.TimeUnit
import javax.inject.Qualifier
import javax.inject.Singleton

@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class ApplicationScope

@Module
@InstallIn(SingletonComponent::class)
object AppModule {

    @Provides @Singleton
    fun provideIdentityKeyStore(@ApplicationContext context: Context): IdentityKeyStore =
        KeystoreIdentityKeyStore(context)

    /**
     * Shared OkHttp client for app-side HTTP work (DoH, ipdeny CIDR refresh).
     * One client = one connection pool = one dispatcher thread pool, instead
     * of leaking thread pools on every per-call instantiation.
     */
    @Provides @Singleton
    fun provideOkHttpClient(): OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    /**
     * Application-lifetime CoroutineScope for fire-and-forget work that must
     * outlive a calling component (Activity, ViewModel, Service, TileService).
     * Used by the Quick Settings tile to dispatch a connect that must survive
     * `onStopListening` arriving the moment the shade collapses after a tap —
     * a viewModelScope-style cancellation here would silently drop the
     * service-start intent.
     */
    @Provides @Singleton @ApplicationScope
    fun provideApplicationScope(): CoroutineScope =
        CoroutineScope(SupervisorJob() + Dispatchers.IO)
}
