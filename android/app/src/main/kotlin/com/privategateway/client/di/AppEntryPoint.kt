package com.privategateway.client.di

import com.privategateway.client.data.ServerRepository
import com.privategateway.client.data.SettingsRepository
import com.privategateway.client.notify.ProtectionNotifier
import com.privategateway.client.subscriptions.SubscriptionBackground
import com.privategateway.client.subscriptions.SubscriptionUpdater
import com.privategateway.client.subscriptions.SubscriptionTransport
import com.privategateway.client.vpn.NetworkAccess
import com.privategateway.client.vpn.TunnelMonitor
import dagger.hilt.EntryPoint
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent

/** Доступ к общим частям приложения без внедрения — для проверок на устройстве (androidTest) и системных точек входа. */
@EntryPoint
@InstallIn(SingletonComponent::class)
interface AppEntryPoint {
    fun servers(): ServerRepository

    fun settings(): SettingsRepository

    fun monitor(): TunnelMonitor

    fun updater(): SubscriptionUpdater
    fun subscriptionTransport(): SubscriptionTransport

    fun notifier(): ProtectionNotifier

    fun background(): SubscriptionBackground

    fun access(): NetworkAccess
}
