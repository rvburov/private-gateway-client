package com.privategateway.client.ui

import androidx.annotation.StringRes
import androidx.compose.ui.graphics.Color
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgState

/**
 * Вид состояния защиты — цвета state.* макета: зеленый — защищено, синий — подключение, красный — выключено или ошибка.
 * Состояние всегда показано иконкой и текстом, не только цветом.
 */
data class StateLook(
    val ring: Color,
    val icon: Color,
    val glow: Color,
    val tint: Color,
    val mark: String,
    @param:StringRes val power: Int,
    @param:StringRes val title: Int,
) {
    companion object {
        fun of(state: TunnelState): StateLook = when (state) {
            TunnelState.Connected -> StateLook(
                PgState.Connected.Ring,
                PgState.Connected.Icon,
                PgState.Connected.Glow,
                PgColor.Status.Success.Tint,
                PgIcons.MarkOk,
                R.string.power_label_connected,
                R.string.status_connected_title,
            )

            TunnelState.Connecting, TunnelState.Stopping -> StateLook(
                PgState.Connecting.Ring,
                PgState.Connecting.Icon,
                PgState.Connecting.Glow,
                PgColor.Accent.Tint,
                PgIcons.MarkWait,
                R.string.power_label_connecting,
                R.string.status_connecting_title,
            )

            // Переподключение — вид подключения, как у десктопа: туннель держит трафик, пока идут попытки.
            TunnelState.Reconnecting -> StateLook(
                PgState.Connecting.Ring,
                PgState.Connecting.Icon,
                PgState.Connecting.Glow,
                PgColor.Accent.Tint,
                PgIcons.MarkWait,
                R.string.power_label_reconnecting,
                R.string.status_reconnecting_title,
            )

            TunnelState.Error -> StateLook(
                PgState.Error.Ring,
                PgState.Error.Icon,
                PgState.Error.Glow,
                PgColor.Status.Danger.Tint,
                PgIcons.MarkAlert,
                R.string.power_label_error,
                R.string.power_label_error,
            )

            TunnelState.Off -> StateLook(
                PgState.Off.Ring,
                PgState.Off.Icon,
                PgState.Off.Glow,
                PgColor.Status.Danger.Tint,
                PgIcons.MarkOff,
                R.string.power_label_off,
                R.string.status_off_title,
            )
        }
    }
}

/** Вид чипа проверки: работает, ждет, выключено, предупреждение. */
enum class ChipLook(val tint: Color, val base: Color, val mark: String) {
    Ok(PgColor.Status.Success.Tint, PgColor.Status.Success.Base, PgIcons.ChipOk),
    Waiting(PgColor.Accent.Tint, PgColor.Accent.Base, PgIcons.ChipDots),
    Off(PgColor.Status.Danger.Tint, PgColor.Status.Danger.Base, PgIcons.ChipDash),
    Warning(PgColor.Status.Caution.Tint, PgColor.Status.Caution.Base, PgIcons.ChipWarn),
}

/** Чип защиты на главной и строка на «Проверке»: заголовок, значение, вид и объяснение. */
data class ProtectionChip(
    @param:StringRes val title: Int,
    @param:StringRes val value: Int,
    val look: ChipLook,
    @param:StringRes val text: Int,
)

/**
 * Четыре чипа главной (доска 25): весь трафик, DNS, IPv6, блок при обрыве — по состоянию туннеля. IPv6 закрыт самим
 * Android: у туннеля нет адреса IPv6 (TunPlan). Блок при обрыве на Android — системный: Android сообщает о нем только
 * работающему туннелю, без туннеля — «в настройках Android». [closed] — туннеля нет, а Android закрыл интернет
 * («Блокировать подключения без VPN», решение 445): как сработавший блок десктопа — трафик остановлен, утечки нет.
 */
object ProtectionChips {
    fun of(state: TunnelState, lockdown: Boolean?, closed: Boolean = false): List<ProtectionChip> {
        val connected = state == TunnelState.Connected
        // Переподключение: карта туннеля осталась, трафик ждет — «поднимается», а не «идет напрямую».
        val waiting = state == TunnelState.Connecting || state == TunnelState.Reconnecting || state == TunnelState.Stopping
        fun pick(ok: Int, wait: Int, off: Int, stopped: Int, stoppedLook: ChipLook) = when {
            connected -> ok to ChipLook.Ok
            waiting -> wait to ChipLook.Waiting
            closed -> stopped to stoppedLook
            else -> off to ChipLook.Off
        }

        val (all, allLook) = pick(R.string.check_all_ok, R.string.check_all_waiting, R.string.check_all_off, R.string.check_all_stopped, ChipLook.Warning)
        val (dns, dnsLook) = pick(R.string.check_dns_ok, R.string.check_dns_waiting, R.string.check_dns_off, R.string.check_dns_stopped, ChipLook.Ok)
        val (ipv6, ipv6Look) = pick(R.string.check_ipv6_ok, R.string.check_ipv6_waiting, R.string.check_ipv6_off, R.string.check_ipv6_stopped, ChipLook.Ok)
        val (ks, ksLook) = when {
            closed && !connected && !waiting -> R.string.check_ks_stopped to ChipLook.Ok
            !connected && state != TunnelState.Reconnecting -> R.string.android_check_ks_system to ChipLook.Warning
            lockdown == true -> R.string.check_ks_ok to ChipLook.Ok
            lockdown == false -> R.string.check_ks_off to ChipLook.Off
            else -> R.string.check_ks_warning to ChipLook.Warning
        }

        return listOf(
            ProtectionChip(R.string.check_all_title, all, allLook, R.string.check_all_text),
            ProtectionChip(R.string.check_dns_title, dns, dnsLook, R.string.check_dns_text),
            ProtectionChip(R.string.check_ipv6_title, ipv6, ipv6Look, R.string.check_ipv6_text),
            ProtectionChip(R.string.check_ks_title, ks, ksLook, R.string.android_check_ks_text),
        )
    }
}
