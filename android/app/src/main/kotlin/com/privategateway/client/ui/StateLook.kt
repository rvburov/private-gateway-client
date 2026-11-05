package com.privategateway.client.ui

import androidx.annotation.StringRes
import androidx.compose.ui.graphics.Color
import com.privategateway.client.R
import com.privategateway.client.core.vpn.TunnelState
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgState

/**
 * Что показывает главная (доска 09): состояние туннеля и то, что телефон знает сверх него. [Strict] — туннеля нет, а сеть
 * закрыл Android («Блокировать подключения без VPN», решение 445) — «Блокировка · строгий режим» макета; [Blocked] — защита
 * не включилась или переподключение сдалось, а сеть держит тот же блок Android — «Блокировка»; [Error] — ошибка без блока:
 * трафик идет напрямую; [Leak] — защита включена, а замер внешнего IP увидел реальный — «Утечка IP». Остановка туннеля
 * выглядит как подключение, как и раньше.
 */
enum class ProtectionView {
    Off,
    Strict,
    Connecting,
    Connected,
    Leak,
    Reconnecting,
    Blocked,
    Error,

    /** Пауза для входа в Wi-Fi (решение 557): туннель опущен по просьбе пользователя на 5 минут. */
    Paused,
    ;

    companion object {
        /**
         * [closed] — сеть закрыл Android без туннеля (MainUi.closedWithoutTunnel); [leak] — выход совпал с реальным IP;
         * [paused] — пауза для входа в Wi-Fi. Если и на паузе Android держит сеть закрытой — честнее «Блокировка Android».
         */
        fun of(state: TunnelState, closed: Boolean, leak: Boolean, paused: Boolean = false): ProtectionView = when (state) {
            TunnelState.Off -> if (closed) Strict else if (paused) Paused else Off
            TunnelState.Connecting, TunnelState.Stopping -> Connecting
            TunnelState.Connected -> if (leak) Leak else Connected
            TunnelState.Reconnecting -> Reconnecting
            TunnelState.Error -> if (closed) Blocked else Error
        }
    }
}

/**
 * Вид состояния защиты — цвета state.* макета: зеленый — защищено, синий — подключение, оранжевый — блокировка, красный —
 * выключено, ошибка или утечка. Состояние всегда показано иконкой и текстом, не только цветом.
 */
data class StateLook(
    val ring: Color,
    val icon: Color,
    val glow: Color,
    /** Заливка круга щита при нажатии (state.*.fill). */
    val fill: Color,
    val tint: Color,
    val mark: String,
    @param:StringRes val power: Int,
    @param:StringRes val title: Int,
) {
    companion object {
        /** Вид по одному состоянию туннеля — без того, что знает главная (блок Android, замер IP). */
        fun of(state: TunnelState): StateLook = of(ProtectionView.of(state, closed = false, leak = false))

        fun of(view: ProtectionView): StateLook = when (view) {
            ProtectionView.Connected -> StateLook(
                PgState.Connected.Ring,
                PgState.Connected.Icon,
                PgState.Connected.Glow,
                PgState.Connected.Fill,
                PgColor.Status.Success.Tint,
                PgIcons.MarkOk,
                R.string.power_label_connected,
                R.string.status_connected_title,
            )

            ProtectionView.Connecting -> StateLook(
                PgState.Connecting.Ring,
                PgState.Connecting.Icon,
                PgState.Connecting.Glow,
                PgState.Connecting.Fill,
                PgColor.Accent.Tint,
                PgIcons.MarkWait,
                R.string.power_label_connecting,
                R.string.status_connecting_title,
            )

            // Переподключение — вид подключения, как у десктопа: туннель держит трафик, пока идут попытки.
            ProtectionView.Reconnecting -> StateLook(
                PgState.Connecting.Ring,
                PgState.Connecting.Icon,
                PgState.Connecting.Glow,
                PgState.Connecting.Fill,
                PgColor.Accent.Tint,
                PgIcons.MarkWait,
                R.string.power_label_reconnecting,
                R.string.status_reconnecting_title,
            )

            // Блокировка: трафик остановлен, утечки нет — тон state.blocked, плашка status.warning (ToneBrush десктопа).
            ProtectionView.Strict, ProtectionView.Blocked -> StateLook(
                PgState.Blocked.Ring,
                PgState.Blocked.Icon,
                PgState.Blocked.Glow,
                PgState.Blocked.Fill,
                PgColor.Status.Warning.Tint,
                PgIcons.MarkAlert,
                R.string.power_label_blocked,
                if (view == ProtectionView.Strict) R.string.status_strict_title else R.string.status_blocked_title,
            )

            // «Утечка IP» — тон и знак «Ошибки», подпись под щитом своя (доска 09).
            ProtectionView.Leak -> StateLook(
                PgState.Error.Ring,
                PgState.Error.Icon,
                PgState.Error.Glow,
                PgState.Error.Fill,
                PgColor.Status.Danger.Tint,
                PgIcons.MarkAlert,
                R.string.power_label_leak,
                R.string.status_leak_title,
            )

            // Пауза для входа в Wi-Fi — янтарный тон: защиты нет, но по просьбе пользователя и ненадолго (доска 17).
            ProtectionView.Paused -> StateLook(
                PgState.Blocked.Ring,
                PgState.Blocked.Icon,
                PgState.Blocked.Glow,
                PgState.Blocked.Fill,
                PgColor.Status.Caution.Tint,
                PgIcons.MarkAlert,
                R.string.power_label_paused,
                R.string.status_paused_title,
            )

            ProtectionView.Error -> StateLook(
                PgState.Error.Ring,
                PgState.Error.Icon,
                PgState.Error.Glow,
                PgState.Error.Fill,
                PgColor.Status.Danger.Tint,
                PgIcons.MarkAlert,
                R.string.power_label_error,
                // Телефон: «Ошибка защиты» — службы на телефоне нет (DECISIONS.md, п. 21.5); код PGC-… — строкой ниже.
                R.string.status_error_title_mobile,
            )

            ProtectionView.Off -> StateLook(
                PgState.Off.Ring,
                PgState.Off.Icon,
                PgState.Off.Glow,
                PgState.Off.Fill,
                PgColor.Status.Danger.Tint,
                PgIcons.MarkOff,
                R.string.power_label_off,
                R.string.status_off_title,
            )
        }
    }
}

/**
 * Вид плитки проверки — CheckBrush десктопа и .tone-* макета: фон, фон выбранной плитки, кружок и рамка выбранной, цвет
 * значения. Работает — зеленый, ждет — синий, остановлено (утечки нет) — оранжевый, частично — янтарный, открыто — красный.
 */
enum class ChipLook(val tint: Color, val hover: Color, val base: Color, val text: Color, val mark: String) {
    Ok(PgColor.Status.Success.Tint, PgColor.Status.Success.Hover, PgColor.Status.Success.Base, PgColor.Status.Success.Text, PgIcons.ChipOk),
    Waiting(PgColor.Accent.Tint, PgColor.Accent.Hover, PgColor.Accent.Base, PgColor.Accent.Text, PgIcons.ChipDots),
    Stopped(PgColor.Status.Warning.Tint, PgColor.Status.Warning.Hover, PgColor.Status.Warning.Base, PgColor.Status.Warning.Text, PgIcons.ChipDash),
    Off(PgColor.Status.Danger.Tint, PgColor.Status.Danger.Hover, PgColor.Status.Danger.Base, PgColor.Status.Danger.Text, PgIcons.ChipDash),
    Warning(PgColor.Status.Caution.Tint, PgColor.Status.Caution.Hover, PgColor.Status.Caution.Base, PgColor.Status.Caution.Text, PgIcons.ChipWarn),
}

/** Плитка защиты на главной и строка на «Проверке»: [id] (all, dns, ipv6, ks), заголовок, значение, вид и пояснение. */
data class ProtectionChip(
    val id: String,
    @param:StringRes val title: Int,
    @param:StringRes val value: Int,
    val look: ChipLook,
    @param:StringRes val text: Int,
)

/**
 * Четыре плитки главной (доски 09 и 25): весь трафик, DNS, IPv6, блок при обрыве — по состоянию туннеля, как
 * `ProtectionChecks.For` десктопа. В подключении и переподключении трафик ждет в карте туннеля: «поднимается», а DNS и IPv6
 * уже не уходят мимо — зеленые. IPv6 закрыт самим Android: у туннеля нет адреса IPv6 (TunPlan). Блок при обрыве на Android —
 * системный: Android сообщает о нем только туннелю ([lockdown]); без туннеля и без блока трафик идет напрямую — «выключен».
 * [closed] — туннеля нет, а Android закрыл интернет («Блокировать подключения без VPN», решение 445): как сработавший блок
 * десктопа — трафик остановлен, утечки нет. [leak] — под защитой замер увидел реальный IP: «реальный IP открыт».
 */
object ProtectionChips {
    fun of(state: TunnelState, lockdown: Boolean?, closed: Boolean = false, leak: Boolean = false): List<ProtectionChip> {
        val connected = state == TunnelState.Connected
        // Переподключение: карта туннеля осталась, трафик ждет — «поднимается», а не «идет напрямую».
        val waiting = state == TunnelState.Connecting || state == TunnelState.Reconnecting || state == TunnelState.Stopping
        val stopped = closed && !connected && !waiting
        fun pick(ok: Int, off: Int, stoppedValue: Int) = when {
            connected || waiting -> ok to ChipLook.Ok
            stopped -> stoppedValue to ChipLook.Ok
            else -> off to ChipLook.Off
        }

        val all = when {
            connected && leak -> ProtectionChip("all", R.string.check_all_title, R.string.check_all_same_ip, ChipLook.Warning, R.string.check_all_same_ip_text)
            connected -> ProtectionChip("all", R.string.check_all_title, R.string.check_all_ok, ChipLook.Ok, R.string.check_all_text)
            waiting -> ProtectionChip("all", R.string.check_all_title, R.string.check_all_waiting, ChipLook.Waiting, R.string.check_all_text)
            stopped -> ProtectionChip("all", R.string.check_all_title, R.string.check_all_stopped, ChipLook.Stopped, R.string.check_all_text)
            else -> ProtectionChip("all", R.string.check_all_title, R.string.check_all_off, ChipLook.Off, R.string.check_all_text)
        }

        val (dns, dnsLook) = pick(R.string.check_dns_ok, R.string.check_dns_off, R.string.check_dns_stopped)
        val (ipv6, ipv6Look) = pick(R.string.check_ipv6_ok, R.string.check_ipv6_off, R.string.check_ipv6_stopped)
        val (ks, ksLook) = when {
            stopped -> R.string.check_ks_stopped to ChipLook.Ok
            !connected && !waiting -> R.string.check_ks_off to ChipLook.Off
            lockdown == true -> R.string.check_ks_ok to ChipLook.Ok
            lockdown == false -> R.string.check_ks_off to ChipLook.Off
            // До Android 10 система не говорит, включен ли блок без VPN.
            connected -> R.string.check_ks_warning to ChipLook.Warning
            // Подключение: туннелю Android еще ничего не сообщил — блок там, в его настройках.
            else -> R.string.android_check_ks_system to ChipLook.Warning
        }

        return listOf(
            all,
            ProtectionChip("dns", R.string.check_dns_title, dns, dnsLook, R.string.check_dns_text),
            ProtectionChip("ipv6", R.string.check_ipv6_title, ipv6, ipv6Look, R.string.check_ipv6_text),
            ProtectionChip("ks", R.string.check_ks_title, ks, ksLook, R.string.android_check_ks_text),
        )
    }
}
