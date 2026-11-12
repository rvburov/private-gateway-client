package com.privategateway.client.di

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.preferencesDataStoreFile
import com.privategateway.client.account.AccountStore
import com.privategateway.client.account.DeviceAccountSource
import com.privategateway.client.core.account.AccountSource
import com.privategateway.client.core.inspect.ProbeRunner
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.KeystoreSecretBox
import com.privategateway.client.data.SecretBox
import com.privategateway.client.data.ServerDao
import com.privategateway.client.data.SubscriptionDao
import com.privategateway.client.inspect.DevicePhoneHints
import com.privategateway.client.inspect.DeviceReportFiles
import com.privategateway.client.inspect.DeviceWebFetch
import com.privategateway.client.inspect.PhoneHintSource
import com.privategateway.client.inspect.PhoneNetworkReader
import com.privategateway.client.inspect.PhoneNetworkSource
import com.privategateway.client.inspect.ReportFiles
import com.privategateway.client.inspect.WebFetch
import com.privategateway.client.ui.qr.DeviceQrImages
import com.privategateway.client.ui.qr.QrImages
import com.privategateway.client.ui.servers.DeviceImportFiles
import com.privategateway.client.ui.servers.ImportFiles
import com.privategateway.client.vpn.AppByteCounters
import com.privategateway.client.vpn.ByteCounters
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.DeviceTunnelDelay
import com.privategateway.client.vpn.LibXrayEngine
import com.privategateway.client.vpn.NetworkDns
import com.privategateway.client.vpn.ServiceTunnelControl
import com.privategateway.client.vpn.SystemNetworkDns
import com.privategateway.client.vpn.TunnelControl
import com.privategateway.client.vpn.TunnelDelay
import com.privategateway.client.subscriptions.DeviceSubscriptionTransport
import com.privategateway.client.subscriptions.SubscriptionTransport
import com.privategateway.client.updates.AppUpdates
import com.privategateway.client.updates.UpdateSource
import dagger.Binds
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

@Module
@InstallIn(SingletonComponent::class)
object AppModule {
    @Provides
    @Singleton
    fun database(@ApplicationContext context: Context): AppDatabase = AppDatabase.open(context)

    @Provides
    fun serverDao(database: AppDatabase): ServerDao = database.servers()

    @Provides
    fun subscriptionDao(database: AppDatabase): SubscriptionDao = database.subscriptions()

    /** Замеры инспектора (DNS, TCP, TLS) — доверенные корни телефона. */
    @Provides
    fun probeRunner(): ProbeRunner = ProbeRunner()

    @Provides
    @Singleton
    fun settings(@ApplicationContext context: Context): DataStore<Preferences> =
        PreferenceDataStoreFactory.create { context.preferencesDataStoreFile("settings") }

    /**
     * Вход в аккаунт сайта и uid устройства — свой файл мимо настроек: резервная копия настроек его не берет, а бэкап и перенос
     * Android выключены (решение 617).
     */
    @Provides
    @Singleton
    @AccountStore
    fun accountStore(@ApplicationContext context: Context): DataStore<Preferences> =
        PreferenceDataStoreFactory.create { context.preferencesDataStoreFile("account") }
}

@Module
@InstallIn(SingletonComponent::class)
abstract class BindingsModule {
    @Binds
    abstract fun subscriptionTransport(transport: DeviceSubscriptionTransport): SubscriptionTransport

    @Binds
    @Singleton
    abstract fun secretBox(box: KeystoreSecretBox): SecretBox

    @Binds
    @Singleton
    abstract fun coreEngine(engine: LibXrayEngine): CoreEngine

    @Binds
    @Singleton
    abstract fun tunnelControl(control: ServiceTunnelControl): TunnelControl

    @Binds
    @Singleton
    abstract fun networkDns(dns: SystemNetworkDns): NetworkDns

    @Binds
    @Singleton
    abstract fun qrImages(images: DeviceQrImages): QrImages

    @Binds
    @Singleton
    abstract fun importFiles(files: DeviceImportFiles): ImportFiles

    @Binds
    @Singleton
    abstract fun phoneNetworks(reader: PhoneNetworkReader): PhoneNetworkSource

    @Binds
    abstract fun webFetch(fetch: DeviceWebFetch): WebFetch

    @Binds
    abstract fun byteCounters(counters: AppByteCounters): ByteCounters

    @Binds
    abstract fun phoneHints(hints: DevicePhoneHints): PhoneHintSource

    @Binds
    abstract fun updates(updates: AppUpdates): UpdateSource

    @Binds
    abstract fun tunnelDelay(delay: DeviceTunnelDelay): TunnelDelay

    @Binds
    abstract fun reportFiles(files: DeviceReportFiles): ReportFiles

    @Binds
    abstract fun accountSource(source: DeviceAccountSource): AccountSource
}
