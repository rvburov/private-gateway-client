package com.privategateway.client.core.account

import java.time.Instant

/** Участник сайта, как его возвращает API приложения: почта и имя для карточки «Аккаунт» в «Настройках». */
data class AccountUser(val email: String, val displayName: String)

/** Устройство в аккаунте сайта: номер на сайте и имя, под которым его видит администратор. */
data class AccountDevice(val id: Long, val name: String)

/**
 * Вход на сайт: токен устройства (секрет — только в хранилище ОС), до какого времени сайт его принимает без проверки, участник
 * и устройство. В [toString] токена нет: запись не должна попасть в журнал или отчет даже случайно.
 */
data class AccountSession(val token: String, val expiresAt: Instant, val user: AccountUser, val device: AccountDevice) {
    override fun toString(): String = "AccountSession(expiresAt=$expiresAt, device=${device.id})"
}

/**
 * Устройство при входе: [uid] — случайный идентификатор установки (создается один раз и не переносится резервной копией),
 * имя для администратора, платформа и версия приложения. Ограничения — как у сайта (AppDeviceIn).
 */
data class DeviceInfo(val uid: String, val name: String, val appVersion: String, val platform: String = PLATFORM) {
    companion object {
        const val PLATFORM = "android"

        /** Длина имени устройства у сайта. */
        const val NAME_MAX = 100

        private val UID = Regex("^[A-Za-z0-9_-]{16,64}$")

        /** Подходит ли [uid] сайту: GUID с дефисами подходит. */
        fun validUid(uid: String): Boolean = UID.matches(uid)

        /**
         * Имя телефона для сайта: «Производитель Модель» без повтора производителя («Google Pixel 8», а не «Google Google
         * Pixel 8»), лишние пробелы и управляющие знаки убраны, не длиннее [NAME_MAX]; пусто — «Android».
         */
        fun phoneName(manufacturer: String?, model: String?): String {
            val maker = clean(manufacturer)
            val device = clean(model)
            val name = when {
                maker.isEmpty() -> device
                device.isEmpty() -> maker
                device.startsWith(maker, ignoreCase = true) -> device
                else -> "$maker $device"
            }
            return name.take(NAME_MAX).trim().ifEmpty { "Android" }
        }

        private fun clean(text: String?): String =
            text.orEmpty().filter { !it.isISOControl() }.split(' ').filter { it.isNotBlank() }.joinToString(" ")
    }
}

/** Почему вход не выполнен — текст account.error.<…> (ветки — в Texts приложения). */
enum class AccountError {
    /** 401: неверная почта или пароль; так же — 401 с незнакомым кодом сайта. */
    Invalid,

    /** 400: код 2FA или резервный код неверный или уже использован. */
    Code,

    /** 403: у администратора двухфакторная защита на сайте еще не настроена — предложить открыть сайт. */
    SetupRequired,

    /** 409: устройств у участника уже столько, сколько разрешает сайт (и 409 с незнакомым кодом), — страница устройств. */
    DeviceLimit,

    /** 409 device_change_limit: за 30 дней добавлено больше трех новых устройств — страница устройств. */
    DeviceChangeLimit,

    /** 429: слишком много попыток; минуты — в [LoginResult.Failed.retryMinutes]. */
    RateLimited,

    /** 422: сайт не принял поля (почта, код не из 6 цифр). */
    Validation,

    /** Нет связи с сайтом: сеть, DNS, TLS, таймаут. */
    Network,

    /** Сайт ответил ошибкой (5xx) или непонятным ответом. */
    Server,

    /** Хранилище ключей телефона недоступно: токен без защиты не держим, вход не выполняется. */
    Store,
}

/** Итог входа. */
sealed interface LoginResult {
    data class Ok(val session: AccountSession) : LoginResult

    /**
     * 401 code_required: почта и пароль верны, но аккаунт администратора — нужен код 2FA или резервный код. Не ошибка: экран
     * входа переходит ко второму шагу и повторяет вход с кодом.
     */
    data object CodeRequired : LoginResult

    /** Вход не выполнен; [retryMinutes] — только у [AccountError.RateLimited], через сколько минут повторить (не меньше 1). */
    data class Failed(val error: AccountError, val retryMinutes: Int = 0) : LoginResult
}

/** Итог проверки доступа (GET /api/app/status). */
sealed interface StatusResult {
    /** Доступ есть; сайт продлил срок до [expiresAt]. */
    data class Ok(val expiresAt: Instant, val user: AccountUser, val device: AccountDevice) : StatusResult

    /** 401 — и только он: устройство удалено, участник заблокирован или сменил пароль, срок истек. */
    data object Revoked : StatusResult

    /** Сайт не ответил понятно: сеть, таймаут, 5xx, мусор. Это не отзыв — действует последний подтвержденный срок. */
    data object Unreachable : StatusResult
}

/**
 * Итог запроса ленты обновлений приложения (GET /api/app/updates). Ленту разбирает и проверяет UpdateCheck: подпись манифеста
 * сайт подделать не может. В [Ok.toString] ленты нет: в ней ссылки-билеты на файлы, им не место в журнале.
 */
sealed interface UpdatesFeed {
    /** Лента как прислал сайт: JSON не длиннее предела [AccountApi.FEED_MAX_BYTES]. */
    class Ok(val json: String) : UpdatesFeed {
        override fun toString(): String = "Ok(${json.length} chars)"
    }

    /** 401 — и только он: доступ отозван, как у проверки доступа. */
    data object Revoked : UpdatesFeed

    /** Сайт не ответил понятно: сеть, таймаут, 422, 5xx, ответ длиннее предела. Это не отзыв. */
    data object Unreachable : UpdatesFeed

    /** Входа нет: сайт не спрашивали — ленту он дает только по токену устройства. */
    data object SignedOut : UpdatesFeed
}
