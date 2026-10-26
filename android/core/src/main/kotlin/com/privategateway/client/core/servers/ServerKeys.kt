package com.privategateway.client.core.servers

import java.util.Locale

/**
 * Как узнать тот же сервер — правила десктопа (`SqliteServerCatalog`): дубль — тот же адрес, транспорт, защита и данные
 * доступа под любым названием; при обновлении подписки свой сервер узнается сначала так же, потом по выходу — протокол,
 * адрес, порт и данные доступа: поставщик мог поправить SNI или путь. Ключи — только для сравнения в памяти, в них
 * секреты: не писать в журнал и в базу.
 */
object ServerKeys {
    /** Куда подключаться: протокол, адрес, транспорт и защита — без секретов. */
    fun endpoint(server: ServerDefinition): String = listOf(
        server.protocol.name,
        server.host.lowercase(Locale.ROOT),
        server.port.toString(),
        server.transport.kind.name,
        server.transport.path.orEmpty(),
        server.transport.host.orEmpty(),
        server.transport.serviceName.orEmpty(),
        server.security.kind.name,
        server.security.sni.orEmpty(),
        server.security.realityPublicKey.orEmpty(),
    ).joinToString("|")

    /** Дубль при импорте и точное совпадение при обновлении подписки. */
    fun key(server: ServerDefinition): String = endpoint(server) + "|" + credentials(server.credentials)

    /** Выход сервера: протокол, адрес, порт и данные доступа — без транспорта и защиты. */
    fun exit(server: ServerDefinition): String =
        listOf(server.protocol.name, server.host.lowercase(Locale.ROOT), server.port.toString(), credentials(server.credentials)).joinToString("|")

    private fun credentials(credentials: ServerCredentials) = listOf(
        credentials.uuid,
        credentials.password,
        credentials.obfsPassword,
        credentials.privateKey,
        credentials.presharedKey,
    ).joinToString("|") { it.orEmpty() }
}
