package com.privategateway.client.core.vpn

/**
 * «Постоянная VPN» и «Блокировать подключения без VPN» в настройках Android. Android сообщает их только работающему
 * туннелю и только с Android 10 (null — не сообщал), поэтому служба запоминает их при каждом подключении.
 */
data class SystemVpn(val alwaysOn: Boolean? = null, val lockdown: Boolean? = null) {
    val known: Boolean get() = alwaysOn != null || lockdown != null
}

/** Откуда значения на экране: Android сообщил их сейчас, при последнем подключении или не сообщал никогда. */
enum class SystemVpnSource {
    Now,
    LastConnection,
    Unknown,
}

/** Что показать в «Настройках» и на главной. */
data class SystemVpnView(val flags: SystemVpn, val source: SystemVpnSource) {
    companion object {
        /**
         * Под защитой (и во время переподключения: карта туннеля та же) — то, что Android сообщил туннелю; без защиты —
         * запомненное при последнем подключении: в настройках Android с тех пор могли что-то поменять.
         */
        fun of(status: TunnelStatus, remembered: SystemVpn): SystemVpnView {
            val now = SystemVpn(status.alwaysOn, status.lockdown)
            val live = status.state == TunnelState.Connected || status.state == TunnelState.Reconnecting
            return when {
                live && now.known -> SystemVpnView(now, SystemVpnSource.Now)
                remembered.known -> SystemVpnView(remembered, SystemVpnSource.LastConnection)
                else -> SystemVpnView(SystemVpn(), SystemVpnSource.Unknown)
            }
        }

        /**
         * Интернет приложению закрыт Android, а туннеля нет: «Блокировать подключения без VPN» сработал. [blocked] —
         * то, что Android сообщает приложению о его сети сейчас (на переднем плане сеть закрывает только этот блок);
         * под защитой и во время подключения сеть приложения — туннель, подсказка не нужна.
         */
        fun closedWithoutTunnel(state: TunnelState, blocked: Boolean): Boolean =
            blocked && (state == TunnelState.Off || state == TunnelState.Error)
    }
}
