package com.privategateway.client.ui.check

import android.content.Context
import androidx.annotation.StringRes
import androidx.compose.runtime.Composable
import androidx.compose.ui.res.stringResource
import com.privategateway.client.R
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.core.text.SecretMasker
import com.privategateway.client.inspect.PhoneHints
import com.privategateway.client.ui.ChipLook
import com.privategateway.client.ui.MainUi
import com.privategateway.client.ui.ProtectionView
import com.privategateway.client.ui.Texts

/**
 * Текст строки «Проверки» и отчета: из словаря — [id] с подстановками [args] (строки или тексты словаря), или готовый —
 * [literal] (адрес). Один и тот же текст показывает экран (Compose) и пишет отчет (Context).
 */
data class CheckText(@param:StringRes val id: Int = 0, val args: List<Any> = emptyList(), val literal: String? = null) {
    @Composable
    fun resolve(): String {
        if (literal != null) {
            return literal
        }

        val values = args.map { (it as? CheckText)?.resolve() ?: it.toString() }
        return if (values.isEmpty()) stringResource(id) else stringResource(id, *values.toTypedArray())
    }

    fun resolve(context: Context): String {
        if (literal != null) {
            return literal
        }

        val values = args.map { (it as? CheckText)?.resolve(context) ?: it.toString() }
        return if (values.isEmpty()) context.getString(id) else context.getString(id, *values.toTypedArray())
    }

    companion object {
        fun of(@StringRes id: Int, vararg args: Any) = CheckText(id, args.toList())

        fun literal(text: String) = CheckText(literal = text)
    }
}

/**
 * Строка проверки — CheckRowViewModel десктопа: [id] (как в контракте десктопа: tunnel, core, all_traffic, kill_switch, dns,
 * ipv6, exit_ip, other_vpn), заголовок, пояснение (части через « · »), чип и вид — цвет кружка и чипа и значок.
 */
data class CheckRow(val id: String, @param:StringRes val title: Int, val detail: List<CheckText>, val chip: CheckText, val look: ChipLook)

/** Строка «Что мешает» (HindranceRowViewModel десктопа): заголовок, пояснение (части через пробел), чип и вид. */
data class HinderRow(val id: String, val title: CheckText, val detail: List<CheckText>, val chip: CheckText, val look: ChipLook)

/**
 * Строки «Проверки» для телефона (доска 7, CheckPageViewModel десктопа) — по состоянию туннеля, замеру внешнего IP, подсказкам
 * Android и настройкам туннеля. Тексты — diag.* десктопа. Первая строка вместо «Службы» (службы у телефона нет) — туннель:
 * служба VPN Android и карта туннеля. «Блокировка» десктопа на телефоне — переподключение (сервер не отвечает, карта туннеля
 * держит трафик) и сеть, закрытая Android без туннеля («Блокировать подключения без VPN»). Пока идет проверка ([running]), все
 * строки — «Проверяется» и «…».
 */
object CheckRows {
    fun of(ui: MainUi, core: String?, hints: PhoneHints?, tunnel: TunnelSettings, running: Boolean): List<CheckRow> {
        val rows = listOf(
            tunnelRow(ui.view),
            core(ui, core),
            allTraffic(ui.view),
            killSwitch(ui),
            dns(ui.view, tunnel),
            ipv6(ui.view, tunnel),
            exitIp(ui),
            otherVpn(hints),
        )
        if (!running) {
            return rows
        }

        return rows.map { it.copy(detail = listOf(CheckText.of(R.string.diag_checking)), chip = CheckText.of(R.string.diag_state_waiting_chip), look = ChipLook.Waiting) }
    }

    /** Слово ОС в «Правила брандмауэра активны: {0}» — блок держит сам Android (app.js макета, слово engine). */
    private val lockdownWord = CheckText.of(R.string.android_settings_vpn_lockdown)

    private enum class Phase {
        /** Туннель работает: «Защищено» и «Утечка IP». */
        Up,

        /** Туннель поднимается или закрывается. */
        Connecting,

        /** Переподключение: сервер не отвечает, карта туннеля держит трафик — «Блокировка» десктопа. */
        Holding,

        /** Туннеля нет, сеть закрыл Android — трафик остановлен, утечки нет. */
        Stopped,

        /** Туннеля нет, трафик идет напрямую: выключено или ошибка без блока. */
        Off,
    }

    private fun phase(view: ProtectionView): Phase = when (view) {
        ProtectionView.Connected, ProtectionView.Leak -> Phase.Up
        ProtectionView.Connecting -> Phase.Connecting
        ProtectionView.Reconnecting -> Phase.Holding
        ProtectionView.Strict, ProtectionView.Blocked -> Phase.Stopped
        ProtectionView.Off, ProtectionView.Error -> Phase.Off
    }

    private fun row(id: String, @StringRes title: Int, look: ChipLook, @StringRes chip: Int, vararg detail: CheckText) =
        CheckRow(id, title, detail.toList(), CheckText.of(chip), look)

    private fun tunnelRow(view: ProtectionView): CheckRow = when (phase(view)) {
        Phase.Up -> row("tunnel", R.string.fact_tunnel, ChipLook.Ok, R.string.diag_state_ok_chip, CheckText.of(R.string.diag_state_ok_text))
        Phase.Connecting, Phase.Holding ->
            row("tunnel", R.string.fact_tunnel, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_state_waiting_text))
        Phase.Stopped -> row("tunnel", R.string.fact_tunnel, ChipLook.Stopped, R.string.diag_state_stopped_chip, CheckText.of(R.string.diag_state_stopped_text))
        Phase.Off -> row("tunnel", R.string.fact_tunnel, ChipLook.Off, R.string.diag_state_off_chip, CheckText.of(R.string.diag_state_off_text))
    }

    /** Ядро: работает — с версией; остановлено из-за ошибки — и что значит код ошибки (сам код — в журнале). */
    private fun core(ui: MainUi, core: String?): CheckRow {
        val title = R.string.diag_core_title
        val error = ui.view == ProtectionView.Error || ui.view == ProtectionView.Blocked
        return when {
            error -> row(
                "core",
                title,
                ChipLook.Stopped,
                R.string.diag_state_stopped_chip,
                CheckText.of(R.string.diag_core_stopped),
                CheckText.of(Texts.error(ui.status.error)),
            )

            else -> when (phase(ui.view)) {
                Phase.Up -> row("core", title, ChipLook.Ok, R.string.diag_state_ok_chip, CheckText.of(R.string.diag_core_ok, listOfNotNull("Xray-core", core).joinToString(" ")))
                Phase.Connecting -> row("core", title, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_core_waiting))
                Phase.Holding -> row("core", title, ChipLook.Stopped, R.string.diag_core_no_reply_chip, CheckText.of(R.string.diag_core_no_reply))
                Phase.Stopped, Phase.Off -> row("core", title, ChipLook.Off, R.string.diag_state_off_chip, CheckText.of(R.string.diag_core_off))
            }
        }
    }

    private fun allTraffic(view: ProtectionView): CheckRow {
        val title = R.string.diag_all_traffic_title
        return when {
            view == ProtectionView.Leak ->
                row("all_traffic", title, ChipLook.Warning, R.string.diag_all_traffic_same_ip_chip, CheckText.of(R.string.diag_all_traffic_same_ip))

            else -> when (phase(view)) {
                Phase.Up -> row("all_traffic", title, ChipLook.Ok, R.string.diag_state_ok_chip, CheckText.of(R.string.diag_all_traffic_ok))
                Phase.Connecting -> row("all_traffic", title, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_all_traffic_waiting))
                Phase.Holding, Phase.Stopped ->
                    row("all_traffic", title, ChipLook.Stopped, R.string.diag_state_stopped_chip, CheckText.of(R.string.diag_all_traffic_stopped))
                Phase.Off -> row("all_traffic", title, ChipLook.Off, R.string.diag_all_traffic_off_chip, CheckText.of(R.string.diag_all_traffic_off))
            }
        }
    }

    /**
     * Блок при обрыве на Android — системный: «Блокировать подключения без VPN». Android сообщает о нем только работающему
     * туннелю и только с Android 10 ([com.privategateway.client.core.vpn.TunnelStatus.lockdown]); сеть закрыта без туннеля —
     * блок сработал.
     */
    private fun killSwitch(ui: MainUi): CheckRow {
        val title = R.string.diag_kill_switch_title
        return when (phase(ui.view)) {
            Phase.Stopped -> row("kill_switch", title, ChipLook.Ok, R.string.diag_state_ok_chip, CheckText.of(R.string.diag_kill_switch_stopped))
            Phase.Up, Phase.Connecting, Phase.Holding -> when (ui.status.lockdown) {
                true -> row("kill_switch", title, ChipLook.Ok, R.string.diag_state_ok_chip, CheckText.of(R.string.diag_kill_switch_ok, lockdownWord))
                false -> row("kill_switch", title, ChipLook.Off, R.string.diag_state_off_chip, CheckText.of(R.string.diag_kill_switch_off))
                null -> row("kill_switch", title, ChipLook.Warning, R.string.diag_state_warning_chip, CheckText.of(R.string.android_settings_vpn_never))
            }

            Phase.Off -> row("kill_switch", title, ChipLook.Off, R.string.diag_state_off_chip, CheckText.of(R.string.diag_kill_switch_off))
        }
    }

    /** DNS: в туннеле — к DNS ядра; свой DNS в частной сети ядро спрашивает напрямую, мимо сервера (TunnelSettings.lanDns). */
    private fun dns(view: ProtectionView, tunnel: TunnelSettings): CheckRow {
        val title = R.string.diag_dns_title
        return when (phase(view)) {
            Phase.Up, Phase.Connecting, Phase.Holding -> if (tunnel.lanDns != null) {
                row("dns", title, ChipLook.Warning, R.string.diag_dns_own_dns_chip, CheckText.of(R.string.diag_dns_own_dns))
            } else {
                row("dns", title, ChipLook.Ok, R.string.diag_dns_ok_chip, CheckText.of(R.string.diag_dns_ok))
            }

            Phase.Stopped -> row("dns", title, ChipLook.Ok, R.string.diag_dns_ok_chip, CheckText.of(R.string.diag_dns_ok))
            Phase.Off -> row("dns", title, ChipLook.Off, R.string.diag_dns_off_chip, CheckText.of(R.string.diag_dns_off))
        }
    }

    /** IPv6: без «IPv6 через сервер» у туннеля нет IPv6, и Android закрывает его всем (решение 397); с ним — IPv6 в туннеле. */
    private fun ipv6(view: ProtectionView, tunnel: TunnelSettings): CheckRow {
        val title = R.string.diag_ipv6_title
        return when (phase(view)) {
            Phase.Up, Phase.Connecting, Phase.Holding -> if (tunnel.ipv6) {
                row("ipv6", title, ChipLook.Ok, R.string.diag_ipv6_tunnel_chip, CheckText.of(R.string.diag_ipv6_tunnel))
            } else {
                row("ipv6", title, ChipLook.Ok, R.string.diag_ipv6_ok_chip, CheckText.of(R.string.diag_ipv6_ok))
            }

            Phase.Stopped -> row("ipv6", title, ChipLook.Ok, R.string.diag_ipv6_ok_chip, CheckText.of(R.string.diag_ipv6_ok))
            Phase.Off -> row("ipv6", title, ChipLook.Off, R.string.diag_ipv6_off_chip, CheckText.of(R.string.diag_ipv6_off))
        }
    }

    /** IP для сайтов — только измеренный: под защитой — выход сервера, без нее — реальный (MainUi.webAddress). */
    private fun exitIp(ui: MainUi): CheckRow {
        val title = R.string.diag_exit_ip_title
        val address = ui.webAddress
        val measuring = ui.ip.measuring
        fun unknown() = if (measuring) {
            row("exit_ip", title, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_checking))
        } else {
            row("exit_ip", title, ChipLook.Warning, R.string.diag_exit_ip_no_answer_chip, CheckText.of(R.string.diag_exit_ip_no_answer))
        }

        return when {
            ui.view == ProtectionView.Leak ->
                row("exit_ip", title, ChipLook.Off, R.string.diag_exit_ip_same_ip_chip, CheckText.of(R.string.diag_exit_ip_same_ip))

            else -> when (phase(ui.view)) {
                Phase.Up -> if (address != null) {
                    row("exit_ip", title, ChipLook.Ok, R.string.diag_exit_ip_ok_chip, CheckText.literal(address))
                } else {
                    unknown()
                }

                Phase.Connecting -> row("exit_ip", title, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_exit_ip_waiting))
                Phase.Holding, Phase.Stopped ->
                    row("exit_ip", title, ChipLook.Stopped, R.string.diag_exit_ip_stopped_chip, CheckText.of(R.string.diag_exit_ip_stopped))
                Phase.Off -> if (address != null) {
                    row("exit_ip", title, ChipLook.Off, R.string.diag_exit_ip_off_chip, CheckText.of(R.string.diag_exit_ip_off, address))
                } else {
                    unknown()
                }
            }
        }
    }

    /** Другие VPN и прокси: VPN другого приложения, прокси сети или общий прокси Android; еще не читали — «Проверяется». */
    private fun otherVpn(hints: PhoneHints?): CheckRow {
        val title = R.string.diag_other_vpn_title
        return when {
            hints == null -> row("other_vpn", title, ChipLook.Waiting, R.string.diag_state_waiting_chip, CheckText.of(R.string.diag_checking))
            hints.otherVpn -> row("other_vpn", title, ChipLook.Warning, R.string.diag_state_warning_chip, CheckText.of(R.string.diag_other_vpn_vpn_adapter))
            hints.proxy != null || hints.pac != null ->
                row("other_vpn", title, ChipLook.Warning, R.string.diag_state_warning_chip, CheckText.of(R.string.diag_other_vpn_system_proxy))
            else -> row("other_vpn", title, ChipLook.Ok, R.string.diag_other_vpn_ok_chip, CheckText.of(R.string.diag_other_vpn_ok))
        }
    }
}

/**
 * «Что мешает» для телефона (решение 508): VPN другого приложения, прокси сети или общий прокси Android (адрес или скрипт PAC —
 * проверить, отвечает ли он, телефон не может: «не проверен»), частный DNS в строгом режиме — обычно не мешает, как WPAD у
 * десктопа. Ничего — «Ничего не найдено». Еще не читали ([hints] null) — одна строка «Проверяется». «Исправить» у телефона
 * нет: переменных среды нет, а прокси и VPN меняются в настройках Android.
 */
object HinderRows {
    fun of(hints: PhoneHints?): List<HinderRow> {
        if (hints == null) {
            return listOf(HinderRow("checking", CheckText.of(R.string.diag_checking), emptyList(), CheckText.of(R.string.diag_state_waiting_chip), ChipLook.Waiting))
        }

        val rows = mutableListOf<HinderRow>()
        if (hints.otherVpn) {
            rows += HinderRow(
                "other",
                CheckText.of(R.string.diag_hinder_other),
                listOf(CheckText.of(R.string.diag_other_vpn_vpn_adapter)),
                CheckText.of(R.string.diag_hinder_chip_live),
                ChipLook.Warning,
            )
        }

        hints.proxy?.let { proxy ->
            rows += HinderRow(
                "proxy",
                CheckText.of(R.string.diag_hinder_system),
                listOf(CheckText.of(R.string.diag_hinder_detail, proxy, CheckText.of(R.string.diag_hinder_unknown)), CheckText.of(R.string.diag_hinder_how_system)),
                CheckText.of(R.string.diag_hinder_chip_set),
                ChipLook.Warning,
            )
        }

        hints.pac?.let { pac ->
            rows += HinderRow(
                "pac",
                CheckText.of(R.string.diag_hinder_pac),
                listOf(CheckText.of(R.string.diag_hinder_detail, SecretMasker.mask(pac), CheckText.of(R.string.diag_hinder_unknown)), CheckText.of(R.string.diag_hinder_how_system)),
                CheckText.of(R.string.diag_hinder_chip_set),
                ChipLook.Warning,
            )
        }

        hints.strictDns?.let { name ->
            rows += HinderRow(
                "private_dns",
                CheckText.of(R.string.android_inspect_privateDns),
                listOf(CheckText.of(R.string.diag_hinder_value, name)),
                CheckText.of(R.string.diag_hinder_chip_set),
                ChipLook.Ok,
            )
        }

        if (rows.isEmpty()) {
            rows += HinderRow(
                "none",
                CheckText.of(R.string.diag_hinder_none),
                listOf(CheckText.of(R.string.diag_hinder_none_text)),
                CheckText.of(R.string.diag_hinder_chip_none),
                ChipLook.Ok,
            )
        }

        return rows
    }
}
