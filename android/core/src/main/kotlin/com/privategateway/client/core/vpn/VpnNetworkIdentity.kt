package com.privategateway.client.core.vpn

/** Признаки конкретной сети Android: номер не переходит на другую сеть при отключении VPN. */
data class VpnNetworkIdentity(
    val id: Long,
    val vpn: Boolean,
    val ownerUid: Int?,
    val interfaceName: String?,
    val addresses: Set<String>,
)

/**
 * Сеть после establish должна принадлежать этому запуску, а не просто иметь транспорт VPN. На Android 11+ система
 * сообщает владельца. Раньше — только новая сеть с адресами нашей карты при сохранившемся разрешении; старые сети,
 * включая чужую VPN до establish, не подходят. При передаче карты Android может оставить прежде подтвержденную нашу сеть.
 */
class VpnNetworkSelection(
    private val before: Set<Long>,
    private val previousOwn: Long?,
    private val uid: Int,
    private val expectedAddresses: Set<String>,
) {
    fun accepts(candidate: VpnNetworkIdentity, prepared: Boolean): Boolean =
        prepared && candidate.vpn && !candidate.interfaceName.isNullOrEmpty() && candidate.addresses.containsAll(expectedAddresses) &&
            if (candidate.ownerUid != null) candidate.ownerUid == uid else candidate.id !in before || candidate.id == previousOwn
}
