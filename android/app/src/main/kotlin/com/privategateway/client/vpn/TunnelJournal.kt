package com.privategateway.client.vpn

import com.privategateway.client.core.text.SecretMasker
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import javax.inject.Inject
import javax.inject.Singleton

/** Уровень записи журнала — как JournalLevel десктопа: на «Проверке» INF, WRN, ERR, DBG. */
enum class JournalLevel {
    Debug,
    Info,
    Warning,
    Error,
}

/** Запись журнала: когда (мс от эпохи), уровень и текст — уже без секретов. */
data class JournalEntry(val at: Long, val level: JournalLevel, val message: String)

/**
 * Журнал туннеля для «Проверки» (доска 7): последние [CAPACITY] записей службы VPN в памяти процесса, по кругу — старые
 * выпадают. Служба и окно — один процесс, поэтому журнал общий. Каждая запись проходит через SecretMasker: ссылок, UUID,
 * ключей и адресов подписок в нем нет; адресов и названий серверов служба в него не пишет. На диск журнал не пишется: после
 * перезапуска приложения он пуст.
 */
@Singleton
class TunnelJournal @Inject constructor() {
    private val state = MutableStateFlow<List<JournalEntry>>(emptyList())

    /** Записи по порядку: последняя — новая. */
    val entries: StateFlow<List<JournalEntry>> = state.asStateFlow()

    fun info(message: String) = add(JournalLevel.Info, message)

    fun warning(message: String) = add(JournalLevel.Warning, message)

    fun error(message: String) = add(JournalLevel.Error, message)

    fun add(level: JournalLevel, message: String, at: Long = System.currentTimeMillis()) {
        val entry = JournalEntry(at, level, SecretMasker.mask(message))
        state.update { (it + entry).takeLast(CAPACITY) }
    }

    companion object {
        /** Сколько записей хранится — как у журнала туннеля iOS. */
        const val CAPACITY = 200
    }
}
