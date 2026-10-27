package com.privategateway.client.core.notify

import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.vpn.TunnelErrors
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import org.junit.jupiter.api.Test
import org.junit.jupiter.params.ParameterizedTest
import org.junit.jupiter.params.provider.ValueSource
import java.io.File
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Какое уведомление дает событие (доска 22) — случаи `NotificationPolicyTests` десктопа там, где Android их знает. */
class NotificationPolicyTest {
    private fun status(state: TunnelState, error: String? = null) = TunnelStatus(state, serverId = "srv-1", error = error)

    private val connected = status(TunnelState.Connected)

    private fun forChange(before: TunnelStatus, after: TunnelStatus, lockdown: Boolean = false) =
        NotificationPolicy.forChange(before, after, "Нидерланды 01", lockdown)

    @Test
    fun `lost connection says the internet waits for the vpn`() {
        val lost = assertNotNull(forChange(connected, status(TunnelState.Reconnecting).copy(attempt = 1)))
        assertEquals(NoticeKind.ConnectionLost, lost.kind)
        assertEquals("notify.lost.blocked", lost.textKey, "карта туннеля остается — трафик ждет и без системной блокировки")
        assertEquals("Нидерланды 01", lost.server)
        assertTrue(lost.important)
        assertNull(lost.code)
        assertEquals(listOf(NoticeAction.ChangeServer, NoticeAction.Open), lost.actions)
        assertEquals("notify.lost.blocked", forChange(connected, status(TunnelState.Reconnecting), lockdown = true)?.textKey)

        assertNull(forChange(status(TunnelState.Connecting), status(TunnelState.Reconnecting).copy(attempt = 1)), "еще не было защиты — не обрыв")
    }

    @Test
    fun `further attempts say nothing`() {
        // Входа в Wi-Fi на Android нет: портал входа Android открывает сам — новые попытки молчат.
        val first = status(TunnelState.Reconnecting).copy(attempt = 1)
        assertNull(forChange(first, first.copy(attempt = 2)))
        assertNull(forChange(first.copy(attempt = 2), first.copy(attempt = 3)))
    }

    @Test
    fun `server down and failures carry the reason`() {
        val gone = status(TunnelState.Error, TunnelErrors.SERVER_NOT_RESPONDING)
        val down = assertNotNull(forChange(status(TunnelState.Reconnecting), gone, lockdown = true))
        assertEquals(NoticeKind.ServerDown, down.kind)
        assertEquals("notify.down.blocked", down.textKey, "система держит трафик без VPN")
        assertEquals(listOf(NoticeAction.Retry, NoticeAction.ChooseServer), down.actions)
        assertTrue(down.important)
        assertNull(down.code, "у «Сервер не отвечает» свой текст — код не нужен")
        assertEquals("notify.down.direct", forChange(status(TunnelState.Reconnecting), gone)?.textKey, "без системной блокировки трафик идет напрямую")
        assertEquals(NoticeKind.ServerDown, forChange(connected, gone)?.kind, "«Не повторять» сдается сразу")
        assertNull(forChange(gone, gone.copy(since = 42, lockdown = true)), "то же состояние — свежие цифры")

        val other = assertNotNull(forChange(status(TunnelState.Connecting), status(TunnelState.Error, TunnelErrors.OTHER_VPN), lockdown = true))
        assertEquals(NoticeKind.ProtectionFailed, other.kind)
        assertEquals("notify.failed.blocked", other.textKey)
        assertEquals(TunnelErrors.OTHER_VPN, other.code)
        assertEquals(listOf(NoticeAction.Open), other.actions)

        val core = status(TunnelState.Error, TunnelErrors.CORE_FAILED)
        val failed = assertNotNull(forChange(status(TunnelState.Connecting), core))
        assertEquals(NoticeKind.ProtectionFailed, failed.kind)
        assertEquals("notify.failed.direct", failed.textKey)
        assertEquals(TunnelErrors.CORE_FAILED, failed.code)
        assertTrue(failed.important)

        assertEquals(TunnelErrors.OTHER_VPN, forChange(core, core.copy(error = TunnelErrors.OTHER_VPN))?.code, "другая ошибка — новое уведомление")
        assertEquals(NoticeKind.ServerDown, forChange(core, gone)?.kind)
        assertEquals(Notice.UNKNOWN_CODE, forChange(status(TunnelState.Connecting), status(TunnelState.Error))?.code, "сбой без кода — общий код")
    }

    @Test
    fun `connect and disconnect are ordinary`() {
        val on = assertNotNull(forChange(status(TunnelState.Connecting), connected))
        assertEquals(NoticeKind.Connected, on.kind)
        assertEquals("notify.on.text", on.textKey)
        assertFalse(on.important)
        assertEquals(emptyList(), on.actions)
        assertEquals("Нидерланды 01", on.server)
        assertEquals("notify.on.back", forChange(status(TunnelState.Reconnecting).copy(attempt = 3), connected)?.textKey)
        assertNull(forChange(connected, connected.copy(since = 42, lockdown = true, alwaysOn = true)), "свежие цифры — не событие")

        val off = assertNotNull(forChange(connected, status(TunnelState.Off)))
        assertEquals(NoticeKind.Disconnected, off.kind)
        assertEquals("notify.off.text", off.textKey)
        assertEquals(listOf(NoticeAction.Connect), off.actions)
        assertFalse(off.important)
        assertNull(off.code)
        assertEquals(NoticeKind.Disconnected, forChange(status(TunnelState.Stopping), status(TunnelState.Off))?.kind)
        assertEquals(NoticeKind.Disconnected, forChange(status(TunnelState.Reconnecting), status(TunnelState.Off))?.kind)
        assertNull(forChange(status(TunnelState.Off), status(TunnelState.Off)))
    }

    @Test
    fun `going through connecting and stopping says nothing`() {
        assertNull(forChange(status(TunnelState.Off), status(TunnelState.Connecting)))
        assertNull(forChange(connected, status(TunnelState.Stopping)))
        assertNull(forChange(status(TunnelState.Error, TunnelErrors.CORE_FAILED), status(TunnelState.Connecting)))
    }

    @Test
    fun `protection notices share one tag and subscriptions have their own`() {
        assertEquals(Notice.PROTECTION_TAG, Notice(NoticeKind.ConnectionLost, "k", emptyList()).tag)
        assertEquals(Notice.PROTECTION_TAG, Notice(NoticeKind.Connected, "k", emptyList()).tag)
        assertEquals(Notice.PROTECTION_TAG, Notice(NoticeKind.ProtectionFailed, "k", emptyList(), subscription = "Основная").tag)
        assertEquals("servers-Основная", Notice(NoticeKind.ServerGone, "k", emptyList(), subscription = "Основная").tag)
        assertEquals("servers-Основная", Notice(NoticeKind.ServerChanged, "k", emptyList(), subscription = "Основная").tag)
        assertEquals("servers-Основная", Notice(NoticeKind.ServersNotUpdated, "k", emptyList(), subscription = "Основная").tag)
        assertFalse(Notice(NoticeKind.ServersNotUpdated, "k", emptyList(), subscription = "Основная").important)
        assertEquals(
            setOf(NoticeKind.ConnectionLost, NoticeKind.ServerDown, NoticeKind.ProtectionFailed),
            NoticeKind.entries.filter { it.important }.toSet(),
            "важные — обрыв, сервер не отвечает, защита не включилась",
        )
    }

    @Test
    fun `subscription changes of the selected server`() {
        // Как `NotificationCenter.Announce` десктопа.
        val gone = assertNotNull(NotificationPolicy.forSelected(SelectedChange.Missing, "Германия 02", "Основная"))
        assertEquals(NoticeKind.ServerGone, gone.kind)
        assertEquals("notify.gone.text", gone.textKey)
        assertEquals(listOf(NoticeAction.ChooseOther, NoticeAction.Later), gone.actions)
        assertEquals("Германия 02", gone.server)
        assertEquals("Основная", gone.subscription)

        val changed = assertNotNull(NotificationPolicy.forSelected(SelectedChange.Changed, "Германия 02", "Основная"))
        assertEquals(NoticeKind.ServerChanged, changed.kind)
        assertEquals("notify.changed.text", changed.textKey)
        assertEquals(listOf(NoticeAction.ChooseServer, NoticeAction.Later), changed.actions)
        assertEquals("Германия 02", changed.server)
        assertEquals("Основная", changed.subscription)

        assertNull(NotificationPolicy.forSelected(SelectedChange.None, "Германия 02", "Основная"))

        val stale = NotificationPolicy.forStale("Основная", FailureReason.Forbidden(403))
        assertEquals(NoticeKind.ServersNotUpdated, stale.kind)
        assertEquals("notify.stale.text", stale.textKey)
        assertEquals(listOf(NoticeAction.OpenSubscriptions), stale.actions)
        assertEquals("Основная", stale.subscription)
        assertEquals(FailureReason.Forbidden(403), stale.failure)
        assertNull(stale.server)
    }

    @Test
    fun `level decides what reaches the shade`() {
        val lost = Notice(NoticeKind.ConnectionLost, "k", emptyList())
        val off = Notice(NoticeKind.Disconnected, "k", emptyList())
        val stale = NotificationPolicy.forStale("Основная", FailureReason.Timeout)

        assertTrue(NotificationPolicy.allowed(NotificationLevel.ImportantOnly, lost))
        assertFalse(NotificationPolicy.allowed(NotificationLevel.ImportantOnly, off), "«Только важные»: отключение — обычное")
        assertFalse(NotificationPolicy.allowed(NotificationLevel.ImportantOnly, stale))
        assertTrue(NotificationPolicy.allowed(NotificationLevel.All, off))
        assertTrue(NotificationPolicy.allowed(NotificationLevel.All, stale))
        assertFalse(NotificationPolicy.allowed(NotificationLevel.Off, lost), "выключены — только постоянное уведомление")
        assertFalse(NotificationPolicy.allowed(NotificationLevel.Off, off))
    }

    @Test
    fun `level is important only by default`() {
        assertEquals(NotificationLevel.ImportantOnly, NotificationLevel.DEFAULT)
        assertEquals(NotificationLevel.ImportantOnly, NotificationLevel.read(null), "ничего не сохранено")
        NotificationLevel.entries.forEach { assertEquals(it, NotificationLevel.read(it.name)) }
    }

    @ParameterizedTest
    @ValueSource(strings = ["", "all", "Silent", "2"])
    fun `unknown level is the default`(name: String) {
        assertEquals(NotificationLevel.ImportantOnly, NotificationLevel.read(name))
    }

    @Test
    fun `every key has a text in both languages`() {
        val keys = buildSet {
            NoticeKind.entries.forEach { add(it.titleKey) }
            NoticeAction.entries.forEach { add(it.textKey) }
            val states = TunnelState.entries.flatMap { state ->
                listOf(null, TunnelErrors.CORE_FAILED, TunnelErrors.SERVER_NOT_RESPONDING).map { status(state, it) }
            }
            for (before in states) for (after in states) for (lockdown in listOf(false, true)) {
                forChange(before, after, lockdown)?.let { add(it.textKey) }
            }

            SelectedChange.entries.forEach { change -> NotificationPolicy.forSelected(change, "s", "g")?.let { add(it.textKey) } }
            add(NotificationPolicy.forStale("g", FailureReason.Timeout).textKey)
        }

        // Все ключи текста — от всех событий.
        assertTrue(
            keys.containsAll(listOf("notify.lost.blocked", "notify.down.blocked", "notify.down.direct", "notify.failed.blocked", "notify.failed.direct")),
        )
        assertEquals("notify.action.openSubscriptions", NoticeAction.OpenSubscriptions.textKey)
        assertEquals("notify.action.changeServer", NoticeAction.ChangeServer.textKey)
        val localization = File(System.getProperty("testdata") ?: error("Нет пути к shared/testdata")).resolve("../specifications/localization")
        for (language in listOf("ru", "en")) {
            val texts = Json.parseToJsonElement(localization.resolve("$language.json").readText()).jsonObject
            assertEquals(emptyList(), keys.filter { it !in texts }, language)
        }
    }
}
