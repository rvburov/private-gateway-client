package com.privategateway.client.core.account

import com.privategateway.client.core.links.JsonText
import com.privategateway.client.core.links.JsonTree
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpRetryException
import java.net.HttpURLConnection
import java.net.URI
import java.time.Instant
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeParseException

/** Что приложение спрашивает у сайта: вход, проверка доступа, выход и лента обновлений — шов над [AccountApi], в тестах подделка. */
interface AccountSource {
    /**
     * Вход по почте и паролю; на втором шаге — еще код 2FA ([code]) или резервный код ([recovery]), не больше одного. Первый
     * шаг — без кода: участнику его хватает, администратору сайт ответит [LoginResult.CodeRequired].
     */
    suspend fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult

    suspend fun status(token: String): StatusResult

    /** Выйти на сайте; ошибка сети не мешает выйти в приложении — итога нет. */
    suspend fun logout(token: String)

    /** Лента обновлений приложения по токену устройства; [channel] — stable или beta. */
    suspend fun updates(token: String, channel: String): UpdatesFeed
}

/**
 * API приложения на сайте Private Gateway Web (/api/app/…): только HTTPS на постоянный адрес [SITE]; в тестах — http на
 * 127.0.0.1. Перенаправлениям не следует (токен не уходит на другой адрес), ответ — не больше [maxBytes], [timeoutMs] на
 * соединение и чтение. Решение о доступе принимает сайт; клиент только раскладывает ответ. Тела ошибок и тексты исключений
 * наружу не идут: итог — причина ([AccountError]) или вид ответа ([StatusResult]); из тела ошибки входа читается только код
 * сайта у 401 и 409 ({"error":{"code":"…"}}): им различаются «нужен код» и «частая смена устройств». Токен, пароль, коды и
 * почта никуда, кроме тела запроса и заголовка Authorization, не пишутся. Сеть — блокирующая: вызывать не в главном потоке.
 * Лента обновлений длиннее остальных ответов — у нее свой предел [feedMaxBytes].
 */
class AccountApi(
    private val base: URI = SITE,
    private val openConnection: (URI) -> HttpURLConnection = { it.toURL().openConnection() as HttpURLConnection },
    private val timeoutMs: Int = TIMEOUT_MS,
    private val maxBytes: Int = MAX_BYTES,
    private val userAgent: String = USER_AGENT,
    private val feedMaxBytes: Int = FEED_MAX_BYTES,
) {
    init {
        require(secure(base)) { "Адрес сайта — только https (http — только 127.0.0.1 для тестов)" }
    }

    /** Вход: без кода — первый шаг; с кодом 2FA ([code]) или резервным ([recovery]) — второй, не больше одного из двух. */
    fun login(email: String, password: String, code: String?, recovery: String?, device: DeviceInfo): LoginResult {
        require(code == null || recovery == null) { "Не больше одного кода: 2FA или резервный" }
        val fields = linkedMapOf<String, Any?>("email" to email.trim(), "password" to password)
        if (code != null) {
            fields["code"] = code.trim()
        }

        if (recovery != null) {
            fields["recovery_code"] = recovery.trim()
        }

        fields["device"] = linkedMapOf(
            "uid" to device.uid,
            "name" to device.name,
            "platform" to device.platform,
            "app_version" to device.appVersion,
        )
        val answer = send("POST", "/api/app/login", null, JsonText.value(fields), errorCodes = true)
        return when (answer) {
            is Answer.Failed -> LoginResult.Failed(AccountError.Network)
            is Answer.Status -> when (answer.code) {
                200 -> answer.body?.let { parseSession(it) }?.let { LoginResult.Ok(it) } ?: LoginResult.Failed(AccountError.Server)
                // Незнакомый код сайта — безопасный запасной вариант: 401 — «неверная почта или пароль», 409 — «лимит устройств».
                401 -> if (answer.error == "code_required") LoginResult.CodeRequired else LoginResult.Failed(AccountError.Invalid)
                400 -> LoginResult.Failed(AccountError.Code)
                403 -> LoginResult.Failed(AccountError.SetupRequired)
                409 -> LoginResult.Failed(if (answer.error == "device_change_limit") AccountError.DeviceChangeLimit else AccountError.DeviceLimit)
                429 -> LoginResult.Failed(AccountError.RateLimited, retryMinutes(answer.retryAfter))
                422 -> LoginResult.Failed(AccountError.Validation)
                else -> LoginResult.Failed(AccountError.Server)
            }
        }
    }

    fun status(token: String): StatusResult = when (val answer = send("GET", "/api/app/status", token, null)) {
        is Answer.Failed -> StatusResult.Unreachable
        is Answer.Status -> when (answer.code) {
            200 -> answer.body?.let { parseStatus(it) } ?: StatusResult.Unreachable
            // Только 401 — отзыв доступа; остальное (5xx, 404 перед выкаткой, 403 прокси) — сайт недоступен.
            401 -> StatusResult.Revoked
            else -> StatusResult.Unreachable
        }
    }

    /** Выйти на сайте: true — сайт принял (204); сеть и ошибки — false, выход в приложении идет все равно. */
    fun logout(token: String): Boolean = (send("POST", "/api/app/logout", token, "") as? Answer.Status)?.code in 200..299

    /**
     * Лента обновлений (GET /api/app/updates) для [platform] и [channel] по токену устройства: тело как есть, не длиннее
     * [feedMaxBytes]; проверяет его UpdateCheck. Как у проверки доступа: 401 — отзыв, остальное (422, 5xx, сеть) — сайт
     * недоступен.
     */
    fun updates(token: String, platform: String = DeviceInfo.PLATFORM, channel: String): UpdatesFeed {
        require(platform in PLATFORMS) { "Платформа ленты — одна из $PLATFORMS" }
        require(channel in CHANNELS) { "Канал ленты — один из $CHANNELS" }
        return when (val answer = send("GET", "$UPDATES_PATH?platform=$platform&channel=$channel", token, null, limit = feedMaxBytes)) {
            is Answer.Failed -> UpdatesFeed.Unreachable
            is Answer.Status -> when (answer.code) {
                200 -> answer.body?.let { UpdatesFeed.Ok(it) } ?: UpdatesFeed.Unreachable
                401 -> UpdatesFeed.Revoked
                else -> UpdatesFeed.Unreachable
            }
        }
    }

    private sealed interface Answer {
        /** Сайт ответил: код, тело (только у 200), Retry-After (секунды, у 429) и код ошибки сайта (у 401 и 409 входа). */
        class Status(val code: Int, val body: String?, val retryAfter: String?, val error: String? = null) : Answer

        /** Ответа нет: сеть, DNS, TLS, таймаут, слишком большой ответ. */
        data object Failed : Answer
    }

    /**
     * Запрос к сайту; [errorCodes] — прочитать код ошибки сайта у 401 и 409 (только вход: у проверки любой 401 — отзыв);
     * [limit] — предел тела ответа 200.
     */
    private fun send(method: String, path: String, token: String?, body: String?, errorCodes: Boolean = false, limit: Int = maxBytes): Answer {
        val connection = try {
            openConnection(base.resolve(path))
        } catch (_: IOException) {
            return Answer.Failed
        } catch (_: IllegalArgumentException) {
            return Answer.Failed
        }

        try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.instanceFollowRedirects = false
            connection.useCaches = false
            connection.requestMethod = method
            connection.setRequestProperty("User-Agent", userAgent)
            connection.setRequestProperty("Accept", "application/json")
            if (token != null) {
                connection.setRequestProperty("Authorization", "Bearer $token")
            }

            if (body != null) {
                val bytes = body.toByteArray(Charsets.UTF_8)
                connection.doOutput = true
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8")
                connection.outputStream.use { it.write(bytes) }
            }

            val code = try {
                connection.responseCode
            } catch (e: HttpRetryException) {
                // HttpURLConnection JDK на 401 с телом запроса не отдает код, а бросает это исключение с кодом внутри.
                return Answer.Status(e.responseCode(), null, null)
            }

            if (code < 0) {
                return Answer.Failed
            }

            val text = if (code == 200) read(connection.inputStream, connection.contentLengthLong, limit) ?: return Answer.Failed else null
            val error = if (errorCodes && (code == 401 || code == 409)) errorCode(connection) else null
            return Answer.Status(code, text, connection.getHeaderField("Retry-After"), error)
        } catch (_: IOException) {
            return Answer.Failed
        } catch (_: SecurityException) {
            return Answer.Failed
        } finally {
            connection.disconnect()
        }
    }

    /**
     * Код ошибки сайта из тела {"error":{"code":"…"}}. Остальное тело не разбирается и никуда не попадает; тела нет, оно больше
     * [maxBytes], не JSON или оборвалось — null: причина остается по коду ответа.
     */
    private fun errorCode(connection: HttpURLConnection): String? {
        val text = try {
            connection.errorStream?.let { read(it, connection.contentLengthLong) }
        } catch (_: IOException) {
            null
        }

        return string(text?.let { tree(it) }?.get("error") as? Map<*, *>, "code")
    }

    /** Тело ответа не больше [limit]; больше — как нет ответа (null). */
    private fun read(stream: InputStream, length: Long, limit: Int = maxBytes): String? {
        if (length > limit) {
            return null
        }

        val out = ByteArrayOutputStream()
        stream.use { input ->
            val chunk = ByteArray(8 * 1024)
            while (true) {
                val read = input.read(chunk)
                if (read < 0) {
                    break
                }

                if (out.size() + read > limit) {
                    return null
                }

                out.write(chunk, 0, read)
            }
        }

        return out.toString(Charsets.UTF_8.name())
    }

    companion object {
        /** Сайт Private Gateway Web: адрес постоянный, в настройках не меняется. */
        val SITE: URI = URI("https://private-gateway.duckdns.org")

        const val TIMEOUT_MS = 15_000

        /** Ответ API — несколько сотен байт; 64 КБ хватает с запасом. */
        const val MAX_BYTES = 64 * 1024

        /** Лента обновлений — до пяти манифестов (каждый до 64 КБ) base64 со ссылками: предел как у службы десктопа. */
        const val FEED_MAX_BYTES = 1024 * 1024

        const val USER_AGENT = "PrivateGateway/0.1"

        /** Страницы сайта для кнопок ошибок входа: настроить 2FA (403) и освободить место (409). */
        const val LOGIN_PAGE = "/login"
        const val DEVICES_PAGE = "/devices"

        /** Страница «Скачать клиент» сайта: новые версии APK и запасной путь, если обновление не встало. */
        const val DOWNLOADS_PAGE = "/downloads"

        /** Лента обновлений и ссылки-билеты на ее файлы (их сайт отдает без токена: билет сам — право на файл). */
        const val UPDATES_PATH = "/api/app/updates"
        const val UPDATE_FILES_PATH = "/api/app/updates/files/"

        /** Платформы и каналы ленты — как у сайта; другое сайт отклонит (422), поэтому не уходит вовсе. */
        val PLATFORMS = setOf("windows", "macos", "linux", "android")
        val CHANNELS = setOf("stable", "beta")

        /** Адрес страницы сайта для кнопки под ошибкой. */
        fun page(path: String, base: URI = SITE): URI = base.resolve(path)

        /** https — всегда; http — только на свой компьютер (тесты на 127.0.0.1). */
        fun secure(uri: URI): Boolean {
            val scheme = uri.scheme?.lowercase() ?: return false
            val host = uri.host ?: return false
            return scheme == "https" || (scheme == "http" && loopback(host))
        }

        /** Только буквальный адрес своего компьютера: имя хоста здесь не разрешается, иначе DNS решал бы, можно ли без TLS. */
        private fun loopback(host: String): Boolean {
            val name = host.removePrefix("[").removeSuffix("]").lowercase()
            if (name == "localhost" || name == "::1") {
                return true
            }

            val parts = name.split('.')
            return parts.size == 4 && parts[0] == "127" &&
                parts.all { part -> part.length in 1..3 && part.all { it in '0'..'9' } && part.toInt() <= 255 }
        }

        /** Минуты из Retry-After (секунды): вверх, не меньше 1; нет или не число — 1. */
        fun retryMinutes(header: String?): Int {
            val seconds = header?.trim()?.toLongOrNull()?.takeIf { it > 0 } ?: return 1
            return ((seconds + 59) / 60).coerceIn(1, Int.MAX_VALUE.toLong()).toInt()
        }

        /** Время ISO-8601 из ответа сайта: со смещением («Z», «+00:00») или без него — тогда UTC. */
        fun instant(text: String): Instant? = try {
            OffsetDateTime.parse(text).toInstant()
        } catch (_: DateTimeParseException) {
            try {
                LocalDateTime.parse(text).toInstant(ZoneOffset.UTC)
            } catch (_: DateTimeParseException) {
                null
            }
        }

        private fun tree(text: String): Map<*, *>? = try {
            JsonTree.parse(text).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        }

        private fun string(node: Map<*, *>?, key: String): String? = node?.get(key) as? String

        private fun user(node: Map<*, *>?): AccountUser? {
            val email = string(node, "email") ?: return null
            return AccountUser(email, string(node, "display_name") ?: "")
        }

        private fun device(node: Map<*, *>?): AccountDevice? {
            val id = (node?.get("id") as? JsonTree.JsonNumber)?.text?.toLongOrNull() ?: return null
            return AccountDevice(id, string(node, "name") ?: "")
        }

        /** Ответ входа: без токена, срока, участника или устройства — не вход (ответ непонятный). */
        internal fun parseSession(text: String): AccountSession? {
            val root = tree(text) ?: return null
            val token = string(root, "token")?.takeIf { it.isNotEmpty() } ?: return null
            val expires = string(root, "expires_at")?.let(::instant) ?: return null
            val user = user(root["user"] as? Map<*, *>) ?: return null
            val device = device(root["device"] as? Map<*, *>) ?: return null
            return AccountSession(token, expires, user, device)
        }

        /** Ответ проверки: «status» = «ok» и срок; иначе непонятный ответ — не отзыв, а «сайт недоступен». */
        internal fun parseStatus(text: String): StatusResult.Ok? {
            val root = tree(text) ?: return null
            if (string(root, "status") != "ok") {
                return null
            }

            val expires = string(root, "expires_at")?.let(::instant) ?: return null
            val user = user(root["user"] as? Map<*, *>) ?: return null
            val device = device(root["device"] as? Map<*, *>) ?: return null
            return StatusResult.Ok(expires, user, device)
        }
    }
}
