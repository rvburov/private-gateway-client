package com.privategateway.client.vpn

import android.content.Context
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton

/** Команды туннелю из окна и фоновой работы — шов над [GatewayVpnService], в тестах подделка. */
interface TunnelControl {
    /** Подключиться к выбранному серверу; под защитой — переподключение без окна утечки (решение 410). */
    fun connect()

    fun disconnect()
}

@Singleton
class ServiceTunnelControl @Inject constructor(@param:ApplicationContext private val context: Context) : TunnelControl {
    override fun connect() = GatewayVpnService.connect(context)

    override fun disconnect() = GatewayVpnService.disconnect(context)
}
