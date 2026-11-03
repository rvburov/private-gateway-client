package com.privategateway.client.core.updates

import com.privategateway.client.core.links.JsonTree
import java.security.KeyFactory
import java.security.PublicKey
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.util.Base64

/** Итог «Проверить» в «О программе» Android (DECISIONS.md, п. 8). */
sealed interface UpdateResult {
    /** В приложении нет ключей подписи обновлений: подписанных выпусков еще нет — проверять нечем. */
    data object NotSigned : UpdateResult

    /** Новее установленной подписанной версии нет. */
    data object UpToDate : UpdateResult

    /** Есть новее: версия и страница выпуска (оттуда ставят APK). */
    data class Available(val version: String, val page: String) : UpdateResult

    /** Список выпусков или манифест не загрузились. */
    data object Failed : UpdateResult
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
 * «Проверить» обновление Android (DECISIONS.md, п. 8) — по тем же правилам, что `UpdateChecker` службы десктопа, но только
 * сказать: ставит APK пользователь со страницы выпуска, фоновой проверки и уведомления нет. Выпуски — API GitHub, от новых к
 * старым, пока версия выше установленной: у выпуска есть `update.json` и `update.json.sig` — загрузки выпусков этого
 * репозитория; подпись сходится с ключом приложения (ECDSA P-256, SHA-256, DER в base64 — как у десктопа); версия в
 * манифесте та же, что в теге; раскатка не остановлена (доля 0). Канал — по установленной версии: бета видит и беты,
 * обычная — только обычные выпуски. Не подошел — следующий. Частичная раскатка кнопку не останавливает: проверку попросил
 * сам пользователь, а ставит он сам.
 */
object UpdateCheck {
    const val REPOSITORY = "rvburov/private-gateway-client"
    const val RELEASES = "https://api.github.com/repos/$REPOSITORY/releases?per_page=20"
    const val DOWNLOADS = "https://github.com/$REPOSITORY/releases/download/"

    /** Ответ GitHub и манифест длиннее — не они. */
    const val MAX_BYTES = 256 * 1024

    /**
     * @param installed версия приложения (versionName).
     * @param keys открытые ключи подписи обновлений, PEM (shared/update/keys); нет — [UpdateResult.NotSigned].
     * @param get загрузка по HTTPS: тело или null (нет сети, не 200, длиннее [MAX_BYTES]).
     */
    suspend fun check(installed: String, keys: List<String>, get: suspend (String) -> ByteArray?): UpdateResult {
        val verifiers = keys.mapNotNull(::publicKey)
        if (verifiers.isEmpty()) {
            return UpdateResult.NotSigned
        }

        val current = SemVer.parse(installed) ?: return UpdateResult.Failed
        val listing = get(RELEASES) ?: return UpdateResult.Failed
        val releases = releases(listing.decodeToString()) ?: return UpdateResult.Failed
        val candidates = releases
            .filter { it.version > current && (current.prerelease || !it.version.prerelease) }
            .sortedByDescending { it.version }
        for (release in candidates) {
            val manifestUrl = release.manifest ?: continue
            val signatureUrl = release.signature ?: continue
            val manifest = get(manifestUrl) ?: return UpdateResult.Failed
            val signature = get(signatureUrl) ?: return UpdateResult.Failed
            if (!verify(manifest, signature.decodeToString(), verifiers)) {
                continue
            }

            if (acceptable(manifest.decodeToString(), release, current)) {
                return UpdateResult.Available(release.tag.removePrefix("v"), release.page)
            }
        }

        return UpdateResult.UpToDate
    }

    /** Выпуск из списка GitHub: тег, версия, страница и ссылки на манифест и подпись (не загрузка этого выпуска — null). */
    internal class Release(val tag: String, val version: SemVer, val page: String, val manifest: String?, val signature: String?)

    /** Список выпусков без черновиков и тегов не вида v<SemVer>; не список — null. */
    internal fun releases(json: String): List<Release>? {
        val tree = try {
            JsonTree.parse(json).value
        } catch (_: JsonTree.SyntaxError) {
            return null
        } as? List<*> ?: return null
        return tree.mapNotNull { item ->
            val release = item as? Map<*, *> ?: return@mapNotNull null
            val tag = release["tag_name"] as? String ?: return@mapNotNull null
            val version = tag.takeIf { it.startsWith("v") }?.let { SemVer.parse(it.substring(1)) } ?: return@mapNotNull null
            if (release["draft"] == true) {
                return@mapNotNull null
            }

            val prefix = "$DOWNLOADS$tag/"
            fun asset(name: String): String? = (release["assets"] as? List<*>).orEmpty()
                .mapNotNull { it as? Map<*, *> }
                .firstOrNull { it["name"] == name }
                ?.let { it["browser_download_url"] as? String }
                ?.takeIf { it == prefix + name }
            Release(tag, version, "https://github.com/$REPOSITORY/releases/tag/$tag", asset("update.json"), asset("update.json.sig"))
        }
    }

    /** Подпись манифеста сходится хотя бы с одним ключом. */
    internal fun verify(manifest: ByteArray, signature: String, keys: List<PublicKey>): Boolean {
        val der = try {
            Base64.getDecoder().decode(signature.trim())
        } catch (_: IllegalArgumentException) {
            return false
        }

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

    /** Манифест подписан и про этот выпуск: та же версия, канал по версии, доля раскатки 1–100. */
    private fun acceptable(json: String, release: Release, current: SemVer): Boolean {
        val manifest = try {
            JsonTree.parse(json).value as? Map<*, *>
        } catch (_: JsonTree.SyntaxError) {
            null
        } ?: return false
        val rollout = (manifest["rollout"] as? JsonTree.JsonNumber)?.text?.toIntOrNull() ?: return false
        val channel = manifest["channel"] as? String
        return manifest["version"] == release.tag.removePrefix("v") &&
            channel == (if (release.version.prerelease) "beta" else "stable") &&
            (current.prerelease || channel == "stable") &&
            rollout in 1..100
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
}
