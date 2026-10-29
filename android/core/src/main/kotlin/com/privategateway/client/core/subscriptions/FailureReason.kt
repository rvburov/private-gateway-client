package com.privategateway.client.core.subscriptions

/**
 * Почему подписка не загрузилась — для текста на экране и в уведомлении, как `FailureText` десктопа: 404, 401 и 403 и
 * остальные коды ответа сказаны по-разному. Тексты — import.download.* в приложении; адреса подписки здесь нет.
 */
sealed interface FailureReason {
    /** Сервер ответил 404 — такой подписки нет (import.download.not_found). */
    data object NotFound : FailureReason

    /** Сервер ответил 401 или 403 — доступ запрещен (import.download.forbidden). */
    data class Forbidden(val code: Int) : FailureReason

    /** Другой ответ не 2xx (import.download.status). */
    data class Status(val code: Int) : FailureReason

    /** import.download.too_large */
    data object TooLarge : FailureReason

    /** import.download.timeout */
    data object Timeout : FailureReason

    /** import.download.dns */
    data object NameResolution : FailureReason

    /** import.download.connection */
    data object Connection : FailureReason

    /** import.download.tls */
    data object Tls : FailureReason

    /** import.download.no_servers */
    data object NoServers : FailureReason

    /** import.download.no_address */
    data object NoAddress : FailureReason

    /** import.download.network */
    data object Network : FailureReason
    data object PrivateDns : FailureReason

    companion object {
        /**
         * Причина и код ответа, как их дает загрузка или хранит подписка. Код важен только для [DownloadFailure.Status];
         * без кода (у загрузчика так не бывает) сказать «сервер ответил» нечего — «ошибка сети».
         */
        fun of(failure: DownloadFailure, status: Int?): FailureReason = when (failure) {
            DownloadFailure.Status -> when (status) {
                null -> Network
                404 -> NotFound
                401, 403 -> Forbidden(status)
                else -> Status(status)
            }

            DownloadFailure.TooLarge -> TooLarge
            DownloadFailure.Timeout -> Timeout
            DownloadFailure.NameResolution -> NameResolution
            DownloadFailure.Connection -> Connection
            DownloadFailure.Tls -> Tls
            DownloadFailure.NoServers -> NoServers
            DownloadFailure.NoAddress -> NoAddress
            DownloadFailure.Network -> Network
            DownloadFailure.PrivateDns -> PrivateDns
        }
    }
}
