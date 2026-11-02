package com.privategateway.client.inspect

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.ProxyInfo
import android.net.Uri
import android.os.Build
import android.os.Process
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject

/**
 * Что на телефоне может мешать защите — «Что мешает» десктопа (решение 508) в том, что Android показывает приложению: прокси
 * сети или общий прокси ([proxy] — «узел:порт», [pac] — адрес скрипта автонастройки), VPN другого приложения ([otherVpn]),
 * частный DNS в строгом режиме ([strictDns] — имя его сервера). Переменных среды и прокси WinHTTP у телефона нет.
 */
data class PhoneHints(
    val proxy: String? = null,
    val pac: String? = null,
    val otherVpn: Boolean = false,
    val strictDns: String? = null,
)

/** Читает [PhoneHints]; [tunnelUp] — свой туннель поднят или поднимается. В тестах — подделка. */
fun interface PhoneHintSource {
    fun read(tunnelUp: Boolean): PhoneHints
}

/**
 * Подсказки из сведений Android о сетях — только чтение. VPN свой или чужой: с Android 11 Android называет владельца сети
 * VPN ее же владельцу; раньше (и если не назвал) — свой, пока поднят свой туннель: два VPN сразу у Android не бывает.
 */
class DevicePhoneHints @Inject constructor(@param:ApplicationContext private val context: Context) : PhoneHintSource {
    override fun read(tunnelUp: Boolean): PhoneHints {
        val manager = context.getSystemService(ConnectivityManager::class.java) ?: return PhoneHints()
        var hints = PhoneHints()
        @Suppress("DEPRECATION")
        val all = manager.allNetworks
        for (network in all) {
            val capabilities = manager.getNetworkCapabilities(network) ?: continue
            if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) {
                if (!own(capabilities, tunnelUp)) {
                    hints = hints.copy(otherVpn = true)
                }

                continue
            }

            val properties = manager.getLinkProperties(network) ?: continue
            hints = withProxy(properties.httpProxy, hints)
            val name = properties.privateDnsServerName
            if (properties.isPrivateDnsActive && !name.isNullOrEmpty()) {
                hints = hints.copy(strictDns = name)
            }
        }

        return withProxy(manager.defaultProxy, hints)
    }

    /** Прокси сети: скрипт PAC — его адрес, иначе «узел:порт»; уже найденное не заменяется. */
    private fun withProxy(proxy: ProxyInfo?, hints: PhoneHints): PhoneHints {
        if (proxy == null) {
            return hints
        }

        // Нет скрипта — Uri.EMPTY; тип объявлен явно: на старых Android метод без пометки о null.
        val pac: Uri? = proxy.pacFileUrl
        if (pac != null && pac != Uri.EMPTY) {
            return if (hints.pac == null) hints.copy(pac = pac.toString()) else hints
        }

        val host: String? = proxy.host
        if (host.isNullOrEmpty() || proxy.port <= 0) {
            return hints
        }

        return if (hints.proxy == null) hints.copy(proxy = "$host:${proxy.port}") else hints
    }

    private fun own(capabilities: NetworkCapabilities, tunnelUp: Boolean): Boolean {
        val owner = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) capabilities.ownerUid else Process.INVALID_UID
        return owner == Process.myUid() || owner == Process.INVALID_UID && tunnelUp
    }
}
