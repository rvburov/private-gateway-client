package com.privategateway.client.core.vpn

import kotlinx.coroutines.delay

/**
 * Почему Android отозвал VPN (onRevoke): другое приложение заняло VPN телефона или пользователь выключил наш в настройках
 * Android (DECISIONS.md, п. 4). Android причину не называет, а VPN другого приложения поднимается чуть позже отзыва нашего,
 * поэтому после отзыва ждем его до [WATCH_MS]: появился — «Работает другой VPN» (PGC-NET-010) со своим уведомлением и
 * страницей проблемы, а не «Защита выключена вручную»; не появился — выключено вручную. Первая проверка — после [SETTLE_MS]:
 * до Android 11 Android не называет владельца VPN, и наш, только что опущенный туннель еще может быть виден как чужой.
 */
object OtherVpn {
    const val SETTLE_MS = 500L
    const val STEP_MS = 250L
    const val WATCH_MS = 3_000L

    /** Ждет VPN другого приложения: [seen] — есть ли он сейчас; [pause] — пауза в мс (в тестах — без ожидания). */
    suspend fun tookOver(pause: suspend (Long) -> Unit = { delay(it) }, seen: () -> Boolean): Boolean {
        pause(SETTLE_MS)
        var waited = SETTLE_MS
        while (true) {
            if (seen()) {
                return true
            }

            if (waited >= WATCH_MS) {
                return false
            }

            pause(STEP_MS)
            waited += STEP_MS
        }
    }
}
