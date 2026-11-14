package com.privategateway.client.core.updates

import com.privategateway.client.core.account.AccountApi
import com.privategateway.client.core.account.UpdatesFeed
import com.privategateway.client.core.links.JsonTree
import java.net.URI
import java.net.URISyntaxException
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.PublicKey
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

/** Итог проверки обновления Android: «Проверить» в «О программе» и проверка при показе окна. */
sealed interface UpdateResult {
    /** В приложении нет ключей подписи обновлений: подписанных выпусков еще нет — проверять нечем. */
    data object NotSigned : UpdateResult

    /** Входа нет или доступ отозван: ленту обновлений сайт дает только по аккаунту устройства. */
    data object SignedOut : UpdateResult

    /** Новее установленной подписанной версии нет (или раскатка до этой установки еще не дошла). */
    data object UpToDate : UpdateResult

    /**
     * Есть новее: версия, APK по ссылке-билету сайта ([url], живет 30 минут) с SHA-256 и размером из подписанного манифеста,
     * страница скачивания сайта ([notes], только https; нет — null) и главное из заметок. В [toString] ссылки нет: билет — право
     * на файл, ему не место в журнале.
     */
    data class Available(
        val version: String,
        val url: String,
        val sha256: String,
        val size: Long,
        val notes: String?,
        val highlights: List<String>,
    ) : UpdateResult {
        override fun toString(): String = "Available(version=$version, size=$size)"
    }

    /** Лента не загрузилась или не читается. */
    data object Failed : UpdateResult
}

/** Уже проверенная подписью политика выпуска. Ревизия отделена от версии приложения и доли раскатки. */
data class UpdatePolicy(val channel: String, val version: String, val revision: Long, val sha256: String) {
    init {
        require(channel == UpdateCheck.STABLE || channel == UpdateCheck.BETA)
        require(SemVer.parse(version) != null && version.length <= 200)
        require(revision in 0..MAX_REVISION)
        require(Regex("[0-9a-f]{64}").matches(sha256))
    }

    companion object {
        /** Точное целое также для издателя/сайта на JavaScript; у прежнего формата без поля — 0. */
        const val MAX_REVISION = 9_007_199_254_740_991L
    }
}

/**
 * Версия SemVer: «1.2.3» или «1.3.0-beta.1» (без «+сборки»). Сравнение — по SemVer 2.0: числа, потом предварительная часть
 * (без нее — старше), ее части по очереди — числа как числа и младше слов.
 */
data class SemVer(val major: Int, val minor: Int, val patch: Int, val pre: List<String>) : Comparable<SemVer> {
    val prerelease: Boolean get() = pre.isNotEmpty()

    override fun compareTo(other: SemVer): Int {
        compareValuesBy(this, other, SemVer::major, SemVer::minor, SemVer::patch).takeIf { it != 0 }?.let { return it }
        if (pre.isEmpty() || other.pre.isEmpty()) {
            return other.pre.size.coerceAtMost(1) - pre.size.coerceAtMost(1)
        }

        for (i in 0 until minOf(pre.size, other.pre.size)) {
            val a = pre[i]
            val b = other.pre[i]
            val x = a.toIntOrNull()
            val y = b.toIntOrNull()
            val order = when {
                x != null && y != null -> x.compareTo(y)
                x != null -> -1
                y != null -> 1
                else -> a.compareTo(b)
            }
            if (order != 0) {
                return order
            }
        }

        return pre.size.compareTo(other.pre.size)
    }

    companion object {
        private val PATTERN = Regex("""(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?""")

        fun parse(text: String): SemVer? {
            val match = PATTERN.matchEntire(text) ?: return null
            val (major, minor, patch, pre) = match.destructured
            return SemVer(major.toIntOrNull() ?: return null, minor.toIntOrNull() ?: return null, patch.toIntOrNull() ?: return null,
                if (pre.isEmpty()) emptyList() else pre.split('.'))
        }
    }
}

/**
 * Проверка обновления Android (0.3.0): лента сайта Private Gateway Web по аккаунту устройства вместо GitHub — правила те же,
 * что у службы десктопа. Сайт хранит update.json выпуска байт в байт и отдает его base64 вместе с подписью, а приложение само
 * сверяет подпись ключами из APK (ECDSA P-256, SHA-256, DER в base64): подделать обновление сайт не может. Выпуски — от новых
 * к старым, пока версия выше установленной. Подходит выпуск, у которого подпись сходится, версия в манифесте та же, что в
 * ленте, канал — по версии, раскатка не остановлена (доля 0), в манифесте есть APK ([APK]: SHA-256 и размер), а ссылка на
 * него — билет сайта (начинается с [FILES]). Не подошел — следующий. Канал — по установленной версии: бета видит и беты,
 * обычная — только обычные выпуски. Частичную раскатку проверка сама соблюдает по номеру установки (bucket 0–99, как у
 * десктопа), а по кнопке — нет (bucket = null): попросил сам пользователь.
 */
object UpdateCheck {
    /** Вид файла APK в манифесте выпуска (scripts/release.cs). */
    const val APK = "android-apk"

    const val STABLE = "stable"
    const val BETA = "beta"

    /** Ссылки на файлы обновлений — только билеты сайта с этого начала. */
    val FILES: String = AccountApi.page(AccountApi.UPDATE_FILES_PATH).toString()

    /** Манифест длиннее — не он (как у десктопа). */
    const val MANIFEST_MAX_BYTES = 64 * 1024

    /** Подпись — DER ECDSA P-256 в base64, около 100 знаков; длиннее — не она. */
    private const val SIGNATURE_MAX_CHARS = 1024

    /** Главное из заметок — как у десктопа: до пяти пунктов по 300 знаков. */
    private const val MAX_HIGHLIGHTS = 5
    private const val HIGHLIGHT_MAX_CHARS = 300

    /** Файл выпуска больше 2 ГБ — не он (как у десктопа). */
    private const val MAX_APK_BYTES = 2L * 1024 * 1024 * 1024

    private val SHA256 = Regex("[0-9a-f]{64}")

    /** Билет после начала ссылки: base64url и точка, без «/», «?» и «#» — один путь на сайте. */
    private val TICKET = Regex("[A-Za-z0-9._~-]{1,2048}")

    /**
     * @param installed версия приложения (versionName).
     * @param keys открытые ключи подписи обновлений, PEM (shared/update/keys); нет — [UpdateResult.NotSigned] без запроса.
     * @param feed лента сайта для канала («stable» или «beta»).
     * @param files начало ссылок на файлы (в тестах — свой сервер на 127.0.0.1).
     * @param bucket номер этой установки 0–99 для частичной раскатки; null — раскатку (кроме остановленной) не учитывать.
     * @param remember атомарно сверить и сохранить ревизию подписанной политики; false — старая или противоречивая.
     */
    suspend fun check(
        installed: String,
        keys: List<String>,
        feed: suspend (channel: String) -> UpdatesFeed,
        files: String = FILES,
        bucket: Int? = null,
        remember: suspend (UpdatePolicy) -> Boolean = { true },
        report: (String) -> Unit = {},
    ): UpdateResult {
        require(bucket == null || bucket in 0..99) { "Номер установки — от 0 до 99" }
        val verifiers = keys.mapNotNull(::publicKey)
        if (verifiers.isEmpty()) {
            return UpdateResult.NotSigned
        }

        val current = SemVer.parse(installed) ?: return UpdateResult.Failed
        val json = when (val answer = feed(if (current.prerelease) BETA else STABLE)) {
            is UpdatesFeed.Ok -> answer.json
            UpdatesFeed.Revoked, UpdatesFeed.SignedOut -> return UpdateResult.SignedOut
            UpdatesFeed.Unreachable -> return UpdateResult.Failed
        }

        val releases = releases(json) ?: return UpdateResult.Failed
        val candidates = releases
            .filter { it.version > current && (current.prerelease || !it.version.prerelease) }
            .sortedByDescending { it.version }
        val candidateSet = candidates.toSet()
        var invalid = false
        val accepted = mutableMapOf<Release, Trusted>()
        // Сначала запоминаются все политики, включая стоп и неподходящую долю. Старый дубликат в начале ленты не может
        // обойти более новую остановку той же версии в конце. Неверная подпись в хранилище ничего не меняет.
        for (release in releases) {
            val parsed = trusted(release, verifiers)
            if (parsed == null || !remember(parsed.policy)) {
                // Старые или чужого канала записи не являются предложением обновления, но их действующие подписанные
                // политики тоже запоминаются. Ошибка исторической записи не мешает определить результат для этой версии.
                if (release in candidateSet) {
                    invalid = true
                    report(if (parsed == null) "manifest or signature rejected" else "manifest revision rejected")
                }
            } else {
                accepted[release] = parsed
            }
        }

        for (candidate in candidates) {
            val release = accepted[candidate] ?: continue
            if (!remember(release.policy)) {
                invalid = true
                report("manifest revision rejected")
                continue
            }
            offer(release, files, bucket)?.let { return it }
        }

        return if (invalid) UpdateResult.Failed else UpdateResult.UpToDate
    }

    /** Выпуск из ленты: версия (и ее текст), точные байты манифеста, подпись и ссылки на файлы по видам. Еще не проверен. */
    internal class Release(val version: SemVer, val text: String, val manifest: ByteArray?, val signature: String?, val files: Map<String, String>)

    private class Trusted(val release: Release, val manifest: Map<*, *>, val policy: UpdatePolicy, val rollout: Int)

    /**
     * Лента сайта {"releases":[…]}; не она — null. Выпуск без версии SemVer пропускается, а неверный манифест/подпись
     * сохраняются как отклоненный кандидат. Ссылка без адреса пропускается. Канал говорит подписанный манифест.
     */
    internal fun releases(json: String): List<Release>? {
        val root = try {
            JsonTree.parse(json).value
        } catch (_: JsonTree.SyntaxError) {
            return null
        } as? Map<*, *> ?: return null
        val list = root["releases"] as? List<*> ?: return null
        return list.mapNotNull { item ->
            val release = item as? Map<*, *> ?: return@mapNotNull null
            val text = release["version"] as? String ?: return@mapNotNull null
            val version = SemVer.parse(text) ?: return@mapNotNull null
            val manifest = (release["manifest"] as? String)?.let(::decode)
            val signature = (release["signature"] as? String)?.takeIf { it.length <= SIGNATURE_MAX_CHARS }
            val files = HashMap<String, String>()
            (release["files"] as? Map<*, *>)?.forEach { (kind, file) ->
                val url = (file as? Map<*, *>)?.get("url") as? String
                if (kind is String && url != null) {
                    files[kind] = url
                }
            }

            Release(version, text, manifest, signature, files)
        }
    }

    /** Подпись и обязательные поля — до записи ревизии; ошибка не считается отсутствием обновления. */
    private fun trusted(release: Release, keys: List<PublicKey>): Trusted? {
        val bytes = release.manifest ?: return null
        val signature = release.signature ?: return null
        if (bytes.size > MANIFEST_MAX_BYTES || !verify(bytes, signature, keys)) {
            return null
        }

        val manifest = try {
            JsonTree.parse(bytes.decodeToString()).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        } ?: return null
        val channel = if (release.version.prerelease) BETA else STABLE
        val rollout = (manifest["rollout"] as? JsonTree.JsonNumber)?.text?.toIntOrNull() ?: return null
        val revision = if (manifest.containsKey("revision")) {
            (manifest["revision"] as? JsonTree.JsonNumber)?.text?.toLongOrNull() ?: return null
        } else 0L
        if (manifest["version"] != release.text || manifest["channel"] != channel || rollout !in 0..100 ||
            revision !in 0..UpdatePolicy.MAX_REVISION || release.text.length > 200) {
            return null
        }

        val hash = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
        return Trusted(release, manifest, UpdatePolicy(channel, release.text, revision, hash), rollout)
    }

    /** Доверенный выпуск может не подходить: стоп/доля, нет APK или билета — это не ошибка подписи. */
    private fun offer(trusted: Trusted, files: String, bucket: Int?): UpdateResult.Available? {
        val release = trusted.release
        val manifest = trusted.manifest
        if (trusted.rollout == 0 || (bucket != null && bucket >= trusted.rollout)) {
            return null
        }

        // Нет APK в манифесте — выпуск не для телефона (или собран до 0.3.0): предлагать нечего.
        val apk = (manifest["assets"] as? Map<*, *>)?.get(APK) as? Map<*, *> ?: return null
        val sha256 = (apk["sha256"] as? String)?.takeIf { SHA256.matches(it) } ?: return null
        val size = (apk["size"] as? JsonTree.JsonNumber)?.text?.toLongOrNull()?.takeIf { it in 1..MAX_APK_BYTES } ?: return null
        val url = release.files[APK]?.takeIf { it.startsWith(files) && TICKET.matches(it.substring(files.length)) } ?: return null
        val notes = (manifest["notes"] as? String)?.takeIf(::https)
        val highlights = (manifest["highlights"] as? List<*>).orEmpty()
            .filterIsInstance<String>()
            .take(MAX_HIGHLIGHTS)
            .map { it.take(HIGHLIGHT_MAX_CHARS) }
        return UpdateResult.Available(release.text, url, sha256, size, notes, highlights)
    }

    /** Подпись манифеста сходится хотя бы с одним ключом. */
    internal fun verify(manifest: ByteArray, signature: String, keys: List<PublicKey>): Boolean {
        val der = decode(signature) ?: return false
        return keys.any { key ->
            try {
                Signature.getInstance("SHA256withECDSA").run {
                    initVerify(key)
                    update(manifest)
                    verify(der)
                }
            } catch (_: java.security.GeneralSecurityException) {
                false
            }
        }
    }

    /** Открытый ключ из PEM «BEGIN PUBLIC KEY» (SubjectPublicKeyInfo, EC P-256); не он — null. */
    internal fun publicKey(pem: String): PublicKey? {
        val body = pem.lineSequence().map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("-----") }.joinToString("")
        return try {
            val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(Base64.getDecoder().decode(body)))
            key.takeIf { (it as? java.security.interfaces.ECPublicKey)?.params?.curve?.field?.fieldSize == 256 }
        } catch (_: java.security.GeneralSecurityException) {
            null
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    /** base64 (пробелы по краям — не помеха); не base64 — null. */
    private fun decode(text: String): ByteArray? = try {
        Base64.getDecoder().decode(text.trim())
    } catch (_: IllegalArgumentException) {
        null
    }

    /** Ссылка https с узлом — страница скачивания из манифеста. */
    private fun https(text: String): Boolean = try {
        val uri = URI(text)
        uri.scheme == "https" && !uri.host.isNullOrEmpty()
    } catch (_: URISyntaxException) {
        false
    }
}
