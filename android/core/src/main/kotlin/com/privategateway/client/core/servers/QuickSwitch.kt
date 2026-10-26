package com.privategateway.client.core.servers

/**
 * Быстрая смена на главной («Сменить ▾», доска 25) — как `QuickServers` десктопа: сервер в работе, за ним измеренные с
 * лучшей задержкой. Не измеренные и без ответа не попадают: выбирать среди них вслепую — дело полного списка.
 */
object QuickSwitch {
    /** Строк в списке вместе с сервером в работе. */
    const val SIZE = 3

    /** @param inUse сервер, через который идет защита, иначе выбранный; null — ни того ни другого. */
    fun pick(servers: List<ServerItem>, inUse: ServerItem?): List<ServerItem> {
        val fastest = servers.filter { it.id != inUse?.id && it.latency?.ms != null }.sortedBy { it.latency?.ms }
        return (listOfNotNull(inUse) + fastest).take(SIZE)
    }
}
