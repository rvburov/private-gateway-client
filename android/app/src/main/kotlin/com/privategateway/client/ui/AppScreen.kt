package com.privategateway.client.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.privategateway.client.R
import com.privategateway.client.ui.check.CheckScreen
import com.privategateway.client.ui.home.HomeScreen
import com.privategateway.client.ui.servers.ImportScreen
import com.privategateway.client.ui.servers.ServersScreen
import com.privategateway.client.ui.settings.SettingsScreen
import com.privategateway.client.ui.theme.PgBackground
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgShadow
import com.privategateway.client.ui.theme.PgState
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgShadow
import com.privategateway.client.vpn.TunnelState

/** Разделы островка-меню: Главная · Серверы · [кнопка-щит] · Проверка · Настройки. */
enum class Tab {
    Home,
    Servers,
    Check,
    Settings,
}

/** Действия окна, которые требуют Android (разрешение на VPN, системные настройки). */
class AppActions(
    val power: () -> Unit,
    val disconnect: () -> Unit,
    val openVpnSettings: () -> Unit,
    val clipboard: () -> String?,
)

@Composable
fun AppScreen(viewModel: MainViewModel, actions: AppActions) {
    val ui by viewModel.ui.collectAsStateWithLifecycle()
    val importing by viewModel.importing.collectAsStateWithLifecycle()
    val core by viewModel.core.collectAsStateWithLifecycle()
    var tab by rememberSaveable { mutableStateOf(Tab.Home) }
    var adding by rememberSaveable { mutableStateOf(false) }
    val closeImport: () -> Unit = {
        adding = false
        viewModel.closeImport()
    }

    PgBackground {
        if (adding) {
            BackHandler(onBack = closeImport)
            ImportScreen(importing, actions.clipboard, viewModel::preview, viewModel::add, closeImport)
        } else {
            BackHandler(enabled = tab != Tab.Home) { tab = Tab.Home }
            when (tab) {
                Tab.Home -> HomeScreen(ui, onServers = { tab = Tab.Servers }, onCheck = { tab = Tab.Check }, onAdd = { adding = true })
                Tab.Servers -> ServersScreen(
                    ui,
                    onSelect = viewModel::select,
                    onDelete = { id ->
                        // Защита идет через удаляемый сервер — VPN отключается, другой сервер пользователь выберет сам.
                        if (ui.status.state != TunnelState.Off && ui.status.serverId == id) {
                            actions.disconnect()
                        }

                        viewModel.delete(id)
                    },
                    onAdd = { adding = true },
                )

                Tab.Check -> CheckScreen(ui, core, viewModel::loadCoreVersion)
                Tab.Settings -> SettingsScreen(core, viewModel::loadCoreVersion, actions.openVpnSettings)
            }

            // Сервера нет — кнопка-щит ведет к добавлению: подключаться не к чему.
            val power: () -> Unit = {
                val off = ui.status.state == TunnelState.Off || ui.status.state == TunnelState.Error
                if (off && ui.inUse == null) {
                    adding = true
                } else {
                    actions.power()
                }
            }
            Island(tab, ui.status.state, hasServer = ui.inUse != null, onTab = { tab = it }, onPower = power, Modifier.align(Alignment.BottomCenter))
        }
    }
}

/** Островок внизу: четыре раздела и кнопка-щит в выемке посередине (layout.island, layout.powerButton). */
@Composable
fun Island(tab: Tab, state: TunnelState, hasServer: Boolean, onTab: (Tab) -> Unit, onPower: () -> Unit, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(PgLayout.Island.CornerRadius)
    val lift = PgLayout.PowerButton.Size / 2
    Box(
        modifier
            .fillMaxWidth()
            .navigationBarsPadding()
            .padding(horizontal = PgLayout.Island.SideMarginMobile)
            .padding(bottom = PgLayout.Island.BottomMargin)
            .height(PgLayout.Island.Height + lift),
    ) {
        Row(
            Modifier
                .align(Alignment.BottomCenter)
                .fillMaxWidth()
                .height(PgLayout.Island.Height)
                .pgShadow(PgShadow.Island, shape)
                .clip(shape)
                .background(PgColor.Surface.Card)
                .border(1.dp, PgColor.Line.Border, shape),
            horizontalArrangement = Arrangement.SpaceEvenly,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            NavItem(Tab.Home, tab, PgIcons.NavHome, null, R.string.nav_home, onTab, Modifier.weight(1f))
            NavItem(Tab.Servers, tab, PgIcons.NavServers, PgIcons.NavServersDetail, R.string.nav_servers, onTab, Modifier.weight(1f))
            PowerLabel(state, hasServer, Modifier.width(PgLayout.PowerButton.Size).fillMaxHeight())
            NavItem(Tab.Check, tab, PgIcons.NavCheck, PgIcons.NavCheckDetail, R.string.nav_check, onTab, Modifier.weight(1f))
            NavItem(Tab.Settings, tab, PgIcons.NavSettings, PgIcons.NavSettingsDetail, R.string.nav_settings, onTab, Modifier.weight(1f))
        }

        // Выемка: круг цвета островка за кнопкой (layout.island.bumpRadius).
        Box(
            Modifier
                .align(Alignment.TopCenter)
                .offset(y = lift - PgLayout.Island.BumpRadius + 4.dp)
                .size(PgLayout.Island.BumpRadius * 2)
                .clip(CircleShape)
                .background(PgColor.Surface.Card),
        )
        PowerButton(state, onPower, Modifier.align(Alignment.TopCenter).offset(y = 4.dp))
    }
}

@Composable
private fun NavItem(item: Tab, current: Tab, icon: String, detail: String?, label: Int, onTab: (Tab) -> Unit, modifier: Modifier) {
    val active = item == current
    val color = if (active) PgColor.Accent.Text else PgColor.Text.Muted
    Column(
        modifier
            .fillMaxHeight()
            .semantics { selected = active }
            .clickable(role = Role.Tab) { onTab(item) }
            .testTag("nav.${item.name.lowercase()}"),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box {
            PgIcon(icon, null, color = color, size = 22.dp)
            if (detail != null) {
                PgIcon(detail, null, color = color, size = 22.dp)
            }
        }

        Spacer(Modifier.height(2.dp))
        Text(stringResource(label), color = color, style = if (active) PgType.NavMobileActive else PgType.NavMobile, maxLines = 1)
    }
}

/** Подпись под кнопкой-щитом — состояние словом («Защищено», «Выключено»), не только цветом. */
@Composable
private fun PowerLabel(state: TunnelState, hasServer: Boolean, modifier: Modifier) {
    val look = StateLook.of(state)
    val label = if (!hasServer && state == TunnelState.Off) R.string.power_label_noServer else look.power
    Box(modifier.padding(bottom = 8.dp), contentAlignment = Alignment.BottomCenter) {
        Text(stringResource(label), color = look.icon, style = PgType.NavMobileActive, textAlign = TextAlign.Center, maxLines = 1)
    }
}

/** Кнопка-щит 80 dp: кольцо цвета состояния, щит с отметкой; при подключении по кольцу бежит дуга. */
@Composable
fun PowerButton(state: TunnelState, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val look = StateLook.of(state)
    val on = state == TunnelState.Connected || state == TunnelState.Connecting
    val description = stringResource(if (on) R.string.power_action_disconnect else R.string.power_action_connect)
    val ring = PgLayout.PowerButton.RingWidth
    Box(
        modifier
            .size(PgLayout.PowerButton.Size)
            .shadow(14.dp, CircleShape, clip = false, ambientColor = look.glow.copy(alpha = 1f), spotColor = look.glow.copy(alpha = 1f))
            .clip(CircleShape)
            .background(PgColor.Surface.Solid)
            .border(ring, look.ring, CircleShape)
            .clickable(enabled = state != TunnelState.Stopping, role = Role.Button, onClickLabel = description, onClick = onClick)
            .semantics { contentDescription = description }
            .testTag("power"),
        contentAlignment = Alignment.Center,
    ) {
        if (state == TunnelState.Connecting) {
            val spin = rememberInfiniteTransition(label = "spinner")
            val angle by spin.animateFloat(0f, 360f, infiniteRepeatable(tween(1100, easing = LinearEasing)), label = "angle")
            Canvas(Modifier.size(PgLayout.PowerButton.Size)) {
                val width = ring.toPx()
                drawArc(
                    PgState.Connecting.Spinner,
                    angle,
                    100f,
                    false,
                    topLeft = androidx.compose.ui.geometry.Offset(width / 2, width / 2),
                    size = androidx.compose.ui.geometry.Size(size.width - width, size.height - width),
                    style = Stroke(width, cap = StrokeCap.Round),
                )
            }
        }

        Box {
            PgIcon(PgIcons.Shield, null, color = look.icon, size = PgLayout.PowerButton.IconSize, strokeWidth = 2f)
            PgIcon(look.mark, null, color = look.icon, size = PgLayout.PowerButton.IconSize, strokeWidth = 2.2f)
        }
    }
}
