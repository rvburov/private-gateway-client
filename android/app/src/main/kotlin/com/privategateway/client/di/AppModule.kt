package com.privategateway.client.di

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.Preferences
import androidx.datastore.preferences.preferencesDataStoreFile
import com.privategateway.client.core.subscriptions.SubscriptionDownloader
import com.privategateway.client.core.subscriptions.SubscriptionSource
import com.privategateway.client.data.AppDatabase
import com.privategateway.client.data.KeystoreSecretBox
import com.privategateway.client.data.SecretBox
import com.privategateway.client.data.ServerDao
import com.privategateway.client.data.SubscriptionDao
import com.privategateway.client.ui.qr.DeviceQrImages
import com.privategateway.client.ui.qr.QrImages
import com.privategateway.client.vpn.CoreEngine
import com.privategateway.client.vpn.LibXrayEngine
import com.privategateway.client.vpn.NetworkDns
import com.privategateway.client.vpn.ServiceTunnelControl
import com.privategateway.client.vpn.SystemNetworkDns
import com.privategateway.client.vpn.TunnelControl
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

    /** Загрузка подписок по сети; в тестах — подделка. */
    @Provides
    fun subscriptionSource(): SubscriptionSource = SubscriptionDownloader()

    @Provides
    @Singleton
    fun settings(@ApplicationContext context: Context): DataStore<Preferences> =
        PreferenceDataStoreFactory.create { context.preferencesDataStoreFile("settings") }
}

@Module
@InstallIn(SingletonComponent::class)
abstract class BindingsModule {
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
}
