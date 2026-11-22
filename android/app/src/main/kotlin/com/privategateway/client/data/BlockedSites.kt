package com.privategateway.client.data

import android.os.ParcelFileDescriptor
import com.privategateway.client.core.settings.SitePattern
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.time.Instant
import java.time.ZoneId
import javax.inject.Inject
import javax.inject.Singleton

/** Только подтвержденный отказ маршрутизатора ядра; название сайта не попадает в журнал и отчет. */
data class BlockedSite(val host: String, val count: Int, val lastSeen: Long) {
    val canAllow: Boolean get() = SitePattern.isPlainDomain(host)
}

/** Текущий день и текущий запуск приложения, не история браузера на диске. */
@Singleton
class BlockedSites @Inject constructor() {
    private val state = MutableStateFlow<List<BlockedSite>>(emptyList())
    val entries: StateFlow<List<BlockedSite>> = state.asStateFlow()
    private var generation = 0L

    @Synchronized
    fun begin(): Long = ++generation

    @Synchronized
    fun record(line: String, expected: Long) {
        if (generation == expected) accept(line)
    }

    @Synchronized
    fun prune(at: Long = System.currentTimeMillis()) {
        val day = Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()).toLocalDate()
        state.value = state.value.filter { Instant.ofEpochMilli(it.lastSeen).atZone(ZoneId.systemDefault()).toLocalDate() == day }
    }

    @Synchronized
    fun accept(line: String, at: Long = System.currentTimeMillis()) {
        val host = blockedHost(line) ?: return
        val today = Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault()).toLocalDate()
        val current = state.value.filter { Instant.ofEpochMilli(it.lastSeen).atZone(ZoneId.systemDefault()).toLocalDate() == today }
        val previous = current.firstOrNull { it.host == host }
        val event = BlockedSite(host, (previous?.count ?: 0).let { if (it == Int.MAX_VALUE) it else it + 1 }, at)
        state.value = (listOf(event) + current.filterNot { it.host == host }).take(LIMIT)
    }

    @Synchronized
    fun clear() {
        generation++
        state.value = emptyList()
    }

    companion object {
        const val LIMIT = 250
        private val detour = Regex("^(?:\\d{4}/\\d{2}/\\d{2} \\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)? )?\\[Info\\] (?:\\[\\d+\\] )?app/dispatcher: (?:Hit route rule: \\[[^\\]\\r\\n]*\\] so )?taking detour \\[block\\] for \\[(?:tcp|udp):(.+):(\\d{1,5})\\]\\s*$")

        /** Источник — реальный выбор блокирующего выхода Xray; произвольные сообщения не принимаются. */
        fun blockedHost(line: String): String? {
            if (line.length > 4096) return null
            val match = detour.matchEntire(line) ?: return null
            if (match.groupValues[2].toIntOrNull() !in 1..65535) return null
            val host = match.groupValues[1].removeSurrounding("[", "]")
            if (host.any { it in "/?#@%" }) return null
            if (host.startsWith("198.18.") || host.startsWith("198.19.")) return null
            return SitePattern.normalize(host)?.takeIf { !it.contains('/') && !it.startsWith("*.") && !it.startsWith(SitePattern.GEOSITE) }
        }
    }
}

/**
 * Внутренний журнал маршрутизатора в канал ОС, без файла на диске. Dispatcher пишет цель после SNI/FakeDNS: здесь это
 * домен, когда ядро действительно его определило. Все строки сразу отбрасываются после выделения блокирующего события.
 * Канал действует для любого ядра: runtime-ошибка extra не может обойти маскировку через GoLog. Для истории внутренний
 * уровень повышается до Info только при правилах блока. Канал снимается после остановки ядра.
 */
class BlockedAccess(private val history: BlockedSites, private val scope: CoroutineScope, private val warning: (Boolean) -> Unit = {}) {
    private var writer: ParcelFileDescriptor? = null
    private var reader: ParcelFileDescriptor? = null
    private var job: Job? = null

    fun configure(json: String, enabled: Boolean): String {
        close()
        return try {
            val pipe = ParcelFileDescriptor.createPipe()
            reader = pipe[0]
            writer = pipe[1]
            val root = JSONObject(json)
            val log = root.optJSONObject("log") ?: JSONObject().also { root.put("log", it) }
            log.put("error", "/proc/self/fd/${pipe[1].fd}")
            log.put("access", "none")
            val requestedLevel = log.optString("loglevel")
            val showWarnings = requestedLevel in setOf("debug", "info", "warning")
            val showErrors = requestedLevel != "none"
            if (enabled && requestedLevel != "debug") log.put("loglevel", "info")
            log.put("dnsLog", false)
            val epoch = history.begin()
            job = scope.launch(Dispatchers.IO) {
                runCatching {
                    ParcelFileDescriptor.AutoCloseInputStream(pipe[0]).bufferedReader().useLines { lines ->
                        lines.forEach { line ->
                            if (enabled) history.record(line, epoch)
                            // Ни маршрут сайта, ни остальные внутренние Info/Debug не попадают в общий журнал.
                            // Для сбоев остается категория без адресов и имен сайтов.
                            if (!line.contains("taking detour [") && !line.contains("accepted ")) {
                                if (showErrors && line.contains("[Error]")) warning(true)
                                else if (showWarnings && line.contains("[Warning]")) warning(false)
                            }
                        }
                    }
                }
            }
            root.toString()
        } catch (_: Exception) {
            close()
            // Без частного канала закрываем native-журнал, а не возвращаем его в stdout с произвольными extra-секретами.
            val root = JSONObject(json)
            val log = root.optJSONObject("log") ?: JSONObject().also { root.put("log", it) }
            log.put("loglevel", "none")
            log.put("error", "none")
            log.put("access", "none")
            log.put("dnsLog", false)
            root.toString()
        }
    }

    fun close() {
        runCatching { writer?.close() }
        runCatching { reader?.close() }
        writer = null
        reader = null
        job?.cancel()
        job = null
    }
}
