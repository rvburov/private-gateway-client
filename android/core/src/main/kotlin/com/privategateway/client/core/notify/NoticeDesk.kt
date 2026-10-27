package com.privategateway.client.core.notify

/**
 * Что сделать с уведомлением: [show] — показать в шторке (null — не показывать); [withdrawProtection] — убрать
 * уведомление о защите, которое там висит: беда прошла или сменилась другой.
 */
data class Delivery(val show: Notice?, val withdrawProtection: Boolean)

/**
 * Правила доски 22 — `Deliver` десктопа (NotificationCenter), без ОС и часов. Повтор того же события — не чаще раза в
 * 10 минут ([NoticeThrottle]). Окно на экране — в шторку ничего: окно показывает то же само (на Android всплывашки в
 * окне нет — состояние и так на главной); такое событие считается показанным. «Подключено» и «VPN отключен» — только
 * при скрытом окне. Уровень из настроек решает остальное. Уведомление о защите в шторке одно: новое о другом заменяет
 * его, а не показанное новое убирает прежнее — «Соединение потеряно» не висит, когда защита вернулась. Не
 * потокобезопасен — вызывать по одному.
 */
class NoticeDesk(private val throttle: NoticeThrottle = NoticeThrottle()) {
    // Какое уведомление о защите сейчас в шторке.
    private var protectionShown: NoticeKind? = null

    /**
     * @param now время, мс по одним часам (в приложении — elapsedRealtime).
     * @param windowVisible окно на экране.
     */
    fun deliver(notice: Notice, now: Long, windowVisible: Boolean, level: NotificationLevel): Delivery {
        val state = notice.kind == NoticeKind.Connected || notice.kind == NoticeKind.Disconnected
        if (!throttle.allows(notice, now)) {
            return Delivery(null, withdraw(notice))
        }

        if (!state && windowVisible) {
            throttle.shown(notice, now)
            return Delivery(null, withdraw(notice))
        }

        if ((state && windowVisible) || !NotificationPolicy.allowed(level, notice)) {
            return Delivery(null, withdraw(notice))
        }

        throttle.shown(notice, now)
        if (notice.tag == Notice.PROTECTION_TAG) {
            protectionShown = notice.kind
        }

        return Delivery(notice, false)
    }

    /** Уведомление о защите нажали или смахнули — его в шторке больше нет. */
    fun protectionGone() {
        protectionShown = null
    }

    private fun withdraw(notice: Notice): Boolean {
        val shown = protectionShown
        if (notice.tag == Notice.PROTECTION_TAG && shown != null && shown != notice.kind) {
            protectionShown = null
            return true
        }

        return false
    }
}
