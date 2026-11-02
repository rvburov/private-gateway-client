package com.privategateway.client.ui

import com.privategateway.client.core.inspect.PhoneNetwork
import com.privategateway.client.core.inspect.PhoneNetworkKind

/**
 * Внешний IP для карточки «Сайты видят ваш IP» — только измеренный, тем же запросом, что «Внешний IP» на «Проверке»
 * (Cloudflare trace). [exit] — адрес выхода, измерен под защитой в подключении [session] (его since); [direct] — реальный
 * IP, измерен без защиты в нынешней сети; [checkedAt] — когда в этом подключении закончилась последняя проверка выхода (мс
 * от эпохи); [measuring] — замер идет.
 */
data class WebIp(
    val exit: String? = null,
    val direct: String? = null,
    val session: Long? = null,
    val checkedAt: Long? = null,
    val measuring: Boolean = false,
)

/** Сеть телефона для строки под состоянием (network.*) и для того, когда реальный IP пора мерить заново. */
object HomeNetwork {
    /**
     * Сеть, через которую телефон выходит в интернет: сеть приложения по умолчанию, а под VPN (своим или чужим) — сеть под
     * ним: проверенная Android, кабель раньше Wi-Fi, Wi-Fi раньше сотовой — так выбирает и сам Android. Нет ни одной — null.
     */
    fun pick(networks: List<PhoneNetwork>): PhoneNetwork? {
        val physical = networks.filter { it.kind != PhoneNetworkKind.Vpn }
        return physical.firstOrNull { it.isDefault }
            ?: physical.filter { it.validated }.minByOrNull { rank.indexOf(it.kind) }
            ?: physical.minByOrNull { rank.indexOf(it.kind) }
    }

    /**
     * Чем одна сеть отличается от другой для реального IP: вид, интерфейс и адреса. Другая сеть — прежний реальный IP уже не
     * тот (у десктопа смена сети тоже сбрасывает DirectIp). Сети нет — null.
     */
    fun identity(networks: List<PhoneNetwork>): List<String>? =
        pick(networks)?.let { listOf(it.kind.name, it.name.orEmpty()) + it.addresses.sorted() }

    private val rank = listOf(PhoneNetworkKind.Ethernet, PhoneNetworkKind.Wifi, PhoneNetworkKind.Cellular, PhoneNetworkKind.Other)
}

/** Скрытый IP — звездочками, как Mask десктопа: цифры (и шестнадцатеричные у IPv6) — «*», точки и двоеточия остаются. */
fun maskIp(ip: String): String = buildString {
    for (c in ip) {
        append(if (c in '0'..'9' || c in 'a'..'f' || c in 'A'..'F') '*' else c)
    }
}
