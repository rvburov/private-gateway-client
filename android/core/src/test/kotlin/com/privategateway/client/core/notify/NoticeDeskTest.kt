package com.privategateway.client.core.notify

import com.privategateway.client.core.subscriptions.FailureReason
import com.privategateway.client.core.subscriptions.SelectedChange
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.core.vpn.TunnelStatus
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Правила доски 22 — случаи `NotificationCenterTests` десктопа: повтор, окно, уровень, снятие прошедшей беды. */
class NoticeDeskTest {
    private val desk = NoticeDesk()
    private var now = 1_000_000L
    private var before = TunnelStatus(TunnelState.Connected, serverId = "nl")
    private var window = false
    private var level = NotificationLevel.ImportantOnly
    private val shown = mutableListOf<NoticeKind>()
    private var withdrawn = 0

    private fun move(state: TunnelState, error: String? = null) {
        val after = TunnelStatus(state, serverId = "nl", error = error)
        val notice = NotificationPolicy.forChange(before, after, "Нидерланды 01", lockdown = false)
        before = after
        notice?.let(::deliver)
    }

    private fun deliver(notice: Notice) {
        val delivery = desk.deliver(notice, now, window, level)
        delivery.show?.let { shown += it.kind }
        if (delivery.withdrawProtection) {
            withdrawn++
        }
    }

    @Test
    fun `drop is shown once within ten minutes`() {
        move(TunnelState.Reconnecting)
        assertEquals(listOf(NoticeKind.ConnectionLost), shown)

        move(TunnelState.Connected)
        move(TunnelState.Reconnecting)
        assertEquals(1, shown.size, "повтор того же события — не чаще раза в 10 минут")

        now += NoticeThrottle.REPEAT_AFTER_MS
        move(TunnelState.Connected)
        move(TunnelState.Reconnecting)
        assertEquals(2, shown.size)
    }

    @Test
    fun `window on the screen gets nothing in the shade and the event counts as shown`() {
        window = true
        move(TunnelState.Reconnecting)
        assertTrue(shown.isEmpty(), "окно показывает «Переподключение» само")

        window = false
        move(TunnelState.Connected)
        move(TunnelState.Reconnecting)
        assertTrue(shown.isEmpty(), "событие при открытом окне считается показанным: повтора в шторке нет 10 минут")
    }

    @Test
    fun `level decides what reaches the shade`() {
        move(TunnelState.Off)
        assertTrue(shown.isEmpty(), "«Только важные» (по умолчанию): отключение — обычное")

        level = NotificationLevel.All
        move(TunnelState.Connecting)
        move(TunnelState.Connected)
        assertEquals(listOf(NoticeKind.Connected), shown, "окно скрыто — «Подключено» видно")

        window = true
        move(TunnelState.Off)
        assertEquals(1, shown.size, "окно на экране — «VPN отключен» и так видно")

        level = NotificationLevel.Off
        window = false
        move(TunnelState.Connected)
        move(TunnelState.Reconnecting)
        assertEquals(1, shown.size, "выключены — только постоянное уведомление туннеля")
    }

    @Test
    fun `drop that passed is taken away from the shade once`() {
        move(TunnelState.Reconnecting)
        move(TunnelState.Connected)
        assertEquals(1, withdrawn, "«Только важные»: «Подключено» не показано — «Соединение потеряно» убрано")

        move(TunnelState.Off)
        move(TunnelState.Connected)
        move(TunnelState.Reconnecting)
        assertEquals(1, withdrawn, "убирать уже нечего")
    }

    @Test
    fun `repeat of the same kind does not take the shown one away`() {
        move(TunnelState.Reconnecting)
        move(TunnelState.Connecting)
        move(TunnelState.Connected)
        withdrawn = 0
        val lost = NotificationPolicy.forChange(TunnelStatus(TunnelState.Connected), TunnelStatus(TunnelState.Reconnecting), "Германия 02", false)!!
        desk.deliver(lost, now, window, level).let { assertNull(it.show, "повтор в те же 10 минут") }

        // В шторке уже ничего: «Подключено» убрало «Соединение потеряно».
        val again = NoticeDesk()
        again.deliver(lost, now, window, level)
        val repeat = again.deliver(lost, now + 1, window, level)
        assertNull(repeat.show)
        assertFalse(repeat.withdrawProtection, "тот же вид в шторке — его и показывает повтор, убирать нечего")
    }

    @Test
    fun `pressed notice is no longer in the shade`() {
        move(TunnelState.Reconnecting)
        desk.protectionGone()
        move(TunnelState.Connected)
        assertEquals(0, withdrawn, "нажатое уведомление уже не в шторке")
    }

    @Test
    fun `subscription notices follow the level and never take the protection notice away`() {
        move(TunnelState.Reconnecting)
        val stale = NotificationPolicy.forStale("Основная", FailureReason.Status(503))
        deliver(stale)
        assertEquals(listOf(NoticeKind.ConnectionLost), shown, "обычное при «Только важные»")
        assertEquals(0, withdrawn, "у подписки своя метка — уведомление о защите не трогается")

        level = NotificationLevel.All
        deliver(NotificationPolicy.forSelected(SelectedChange.Missing, "Германия 02", "Основная")!!)
        assertEquals(NoticeKind.ServerGone, shown.last())

        window = true
        now += NoticeThrottle.REPEAT_AFTER_MS
        val delivery = desk.deliver(stale, now, window, level)
        assertNull(delivery.show, "окно на экране — в шторку ничего при любом уровне")
        assertFalse(delivery.withdrawProtection)
    }
}
