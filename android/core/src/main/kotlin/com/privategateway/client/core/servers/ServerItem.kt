package com.privategateway.client.core.servers

/**
 * Сервер для списка, карточки и главной — как `ServerProfile` десктопа, но без адреса и ключа: их знает только
 * запечатанная ссылка (решение 399), поэтому ни поиск, ни подписи списка адрес не видят.
 *
 * @property linkName название из ссылки или подписки; обновление подписки его меняет.
 * @property subscriptionId подписка, из которой пришел сервер; null — добавлен ссылкой («Мои серверы»).
 * @property favorite отмечен звездой.
 * @property latency последний замер; null — ни разу не мерили.
 * @property lastUsedAt когда выбирали последний раз (мс от эпохи); null — не выбирали.
 * @property missingSince когда пропал из подписки (мс от эпохи); null — на месте. Пропавший остается, пока выбран.
 * @property customName свое название пользователя; переживает обновления подписки.
 */
data class ServerItem(
    val id: String,
    val linkName: String,
    val countryCode: String,
    val protocol: ProxyProtocol,
    val security: TransportSecurity,
    val transport: TransportKind,
    val port: Int,
    val subscriptionId: String? = null,
    val favorite: Boolean = false,
    val latency: Latency? = null,
    val lastUsedAt: Long? = null,
    val missingSince: Long? = null,
    val customName: String? = null,
) {
    /** Название в списке, карточке и на главной: свое, если пользователь переименовал. */
    val displayName: String get() = customName ?: linkName

    /** «VLESS · REALITY». */
    val summary: String get() = ServerFacts.summary(protocol, security, transport)

    val encrypted: Boolean get() = ServerFacts.isEncrypted(protocol, security)
}
