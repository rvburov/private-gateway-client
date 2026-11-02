package com.privategateway.client.core.settings

/** Подробность журнала ядра («Журнал ядра», доска 6) — уровень loglevel Xray. Журнал соединений выключен всегда. */
enum class CoreLogLevel(val xray: String) {
    Warning("warning"),
    Info("info"),
    Debug("debug"),
    ;

    companion object {
        /** Из сохраненного имени: нет значения или имя из будущей версии — «Предупреждения». */
        fun read(name: String?): CoreLogLevel = entries.firstOrNull { it.name == name } ?: Warning
    }
}

/** Отпечаток TLS (uTLS) — каким браузером клиент выглядит для сети; [Profile] — как в ссылке или подписке. */
enum class TlsFingerprint(val xray: String?) {
    Profile(null),
    Chrome("chrome"),
    Firefox("firefox"),
    Safari("safari"),
    Random("random"),
    ;

    companion object {
        /** Из сохраненного имени: нет значения или имя из будущей версии — «Из профиля». */
        fun read(name: String?): TlsFingerprint = entries.firstOrNull { it.name == name } ?: Profile
    }
}

/**
 * «Подключение и ядро» телефона (доска 6) — только то, что ядро и туннель телефона выполняют на деле: журнал ядра, MTU,
 * строгие маршруты, определение домена, отпечаток TLS, Mux, фрагментация TLS и таймаут подключения. Значения по умолчанию —
 * как у десктопа (`ConnectionSettings`). Стека TUN нет: вход TUN Xray выбора стека не дает; локального и системного прокси
 * нет — на телефоне только VPN (решение 527). Переподключение хранится отдельно (`ReconnectPolicy`): туннель оно не меняет.
 *
 * @property mtu свой MTU туннеля; null — «Авто» ([AUTO_MTU]).
 * @property strictRoutes программы не могут сами обойти туннель через другую сеть телефона: без
 *   `VpnService.Builder.allowBypass` Android не пускает их мимо карты VPN, даже если программа привяжется к Wi-Fi или
 *   мобильной сети напрямую.
 * @property sniffing имя сайта из TLS, HTTP и QUIC — для правил по доменам («Маршрутизация»).
 * @property connectTimeoutSeconds сколько ждать ответа сервера через туннель (проба связи), секунды.
 */
data class ConnectionSettings(
    val coreLog: CoreLogLevel = CoreLogLevel.Warning,
    val mtu: Int? = null,
    val strictRoutes: Boolean = true,
    val sniffing: Boolean = true,
    val fingerprint: TlsFingerprint = TlsFingerprint.Profile,
    val mux: Boolean = false,
    val tlsFragment: Boolean = false,
    val connectTimeoutSeconds: Int = DEFAULT_TIMEOUT_SECONDS,
) {
    /** MTU карты туннеля и входа TUN ядра: свой из допустимых или «Авто». */
    val tunMtu: Int get() = mtu?.takeIf { validMtu(it) } ?: AUTO_MTU

    /** Срок ответа сервера на пробе связи, мс; срок вне допустимых (из будущей версии) — по умолчанию. */
    val probeTimeoutMs: Long get() = (connectTimeoutSeconds.takeIf { it in TIMEOUT_RANGE } ?: DEFAULT_TIMEOUT_SECONDS) * 1_000L

    companion object {
        /** Свой MTU: от минимального для IPv6 (1280) до обычного Ethernet и Wi-Fi (1500) — иначе пакеты режутся. */
        const val MIN_MTU = 1280
        const val MAX_MTU = 1500

        /** «Авто»: MTU обычной сети — тот, с которым туннель телефона работал до этой настройки. */
        const val AUTO_MTU = 1500

        const val DEFAULT_TIMEOUT_SECONDS = 10

        /** Предлагаемые MTU в порядке меню; null — «Авто». */
        val MTU_CHOICES: List<Int?> = listOf(null, 1500, 1420, 1280)

        /** Предлагаемые сроки ожидания ответа сервера, секунды, в порядке меню (как у десктопа). */
        val TIMEOUT_CHOICES: List<Int> = listOf(10, 5, 20, 30)

        /** Сроки, которые хранилище принимает (как `SqliteSettingsStore` десктопа). */
        private val TIMEOUT_RANGE = 1..120

        fun validMtu(value: Int): Boolean = value in MIN_MTU..MAX_MTU

        /** Свой MTU из поля листа: целое число от 1280 до 1500 одними цифрами, иначе null. */
        fun parseMtu(text: String): Int? {
            val value = text.trim()
            if (value.isEmpty() || value.length > 4 || !value.all { it in '0'..'9' }) {
                return null
            }

            return value.toInt().takeIf { validMtu(it) }
        }

        /**
         * Из сохраненных значений: нет значения, неизвестное имя (из будущей версии) или число вне допустимых — значение по
         * умолчанию, как `SqliteSettingsStore` десктопа.
         */
        fun read(
            coreLog: String?,
            mtu: Int?,
            strictRoutes: Boolean?,
            sniffing: Boolean?,
            fingerprint: String?,
            mux: Boolean?,
            tlsFragment: Boolean?,
            connectTimeoutSeconds: Int?,
        ): ConnectionSettings {
            val defaults = ConnectionSettings()
            return ConnectionSettings(
                coreLog = CoreLogLevel.read(coreLog),
                mtu = mtu?.takeIf { validMtu(it) },
                strictRoutes = strictRoutes ?: defaults.strictRoutes,
                sniffing = sniffing ?: defaults.sniffing,
                fingerprint = TlsFingerprint.read(fingerprint),
                mux = mux ?: defaults.mux,
                tlsFragment = tlsFragment ?: defaults.tlsFragment,
                connectTimeoutSeconds = connectTimeoutSeconds?.takeIf { it in TIMEOUT_RANGE } ?: defaults.connectTimeoutSeconds,
            )
        }
    }
}
