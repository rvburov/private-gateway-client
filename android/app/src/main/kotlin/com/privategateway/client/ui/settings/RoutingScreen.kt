package com.privategateway.client.ui.settings

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.privategateway.client.R
import com.privategateway.client.core.settings.BypassApp
import com.privategateway.client.core.settings.RoutingMode
import com.privategateway.client.core.settings.RoutingSettings
import com.privategateway.client.core.settings.RuleAction
import com.privategateway.client.core.settings.SitePattern
import com.privategateway.client.core.settings.SiteRule
import com.privategateway.client.core.settings.TunnelSettings
import com.privategateway.client.data.PhoneApp
import com.privategateway.client.ui.theme.LocalPgToast
import com.privategateway.client.ui.theme.Overline
import com.privategateway.client.ui.theme.PgCard
import com.privategateway.client.ui.theme.PgColor
import com.privategateway.client.ui.theme.PgDialog
import com.privategateway.client.ui.theme.PgIcon
import com.privategateway.client.ui.theme.PgIcons
import com.privategateway.client.ui.theme.PgInput
import com.privategateway.client.ui.theme.PgLayout
import com.privategateway.client.ui.theme.PgPageHeader
import com.privategateway.client.ui.theme.PgPrimaryButton
import com.privategateway.client.ui.theme.PgRadius
import com.privategateway.client.ui.theme.PgSecondaryButton
import com.privategateway.client.ui.theme.PgSegments
import com.privategateway.client.ui.theme.PgSheet
import com.privategateway.client.ui.theme.PgSpace
import com.privategateway.client.ui.theme.PgSwitch
import com.privategateway.client.ui.theme.PgTone
import com.privategateway.client.ui.theme.PgType
import com.privategateway.client.ui.theme.pgTopDivider

/**
 * «Маршрутизация» телефона (доска 13): шапка с «Назад» стоит, прокручивается список. «Весь трафик» и «Раздельный режим»
 * переключают экран без вопроса (решение 111): предупреждение и «Программы мимо VPN» — только в раздельном режиме, в режиме
 * «Весь трафик» правила «Напрямую» бледнее, и над списком — подпись, что они не действуют. Программы мимо VPN исключает сам
 * Android (addDisallowedApplication); правила для сайтов — правила ядра Xray телефона; geosite нет — базы в приложении нет.
 * Правило открывается листом, «Добавить правило» — тот же лист; «Добавить программу» — лист со списком программ телефона;
 * «Локальная сеть» ведет в «Защиту и утечки» ([onProtection]). Под защитой изменения применяет служба туннеля —
 * переподключением без окна утечки.
 */
@Composable
fun RoutingScreen(
    tunnel: TunnelSettings,
    onTunnel: (TunnelSettings) -> Unit,
    onBack: () -> Unit,
    onProtection: () -> Unit,
    installedApps: suspend () -> List<PhoneApp>,
) {
    // Открытый лист правила: null — закрыт, NEW_RULE — новое, иначе — номер правила в списке.
    var editing by rememberSaveable { mutableStateOf<Int?>(null) }
    var picking by rememberSaveable { mutableStateOf(false) }
    // «Раздельный режим» — через вопрос, как у десктопа (просьба пользователя 2026-11-05); обратно — без вопроса.
    var askingSplit by rememberSaveable { mutableStateOf(false) }
    val routing = tunnel.routing
    val change: (RoutingSettings) -> Unit = { onTunnel(tunnel.copy(routing = it)) }
    Column(Modifier.fillMaxSize().statusBarsPadding().testTag("routing")) {
        Box(Modifier.padding(horizontal = PgSpace.WindowSideMobile)) {
            PgPageHeader(stringResource(R.string.settings_adv_routing_title), onBack)
        }

        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(start = PgSpace.WindowSideMobile, top = PgSpace.L, end = PgSpace.WindowSideMobile, bottom = PgLayout.ScrollBottomPadding),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            ModeCard(routing.mode) {
                if (it == RoutingMode.Split && !routing.split) askingSplit = true else change(routing.copy(mode = it))
            }
            if (routing.split) {
                SplitWarning()
                Section(stringResource(R.string.routing_apps_title)) {
                    if (routing.apps.isEmpty()) {
                        EmptyNote(stringResource(R.string.routing_apps_empty), "routing.apps.empty")
                    } else {
                        PgCard(Modifier.fillMaxWidth(), contentPadding = ListCard) {
                            routing.apps.forEachIndexed { index, app ->
                                AppRow(app, index, divider = index > 0) { change(routing.setApp(app, it)) }
                            }
                        }
                    }

                    DashedButton(stringResource(R.string.routing_apps_add), "routing.apps.add") { picking = true }
                }
            }

            Section(stringResource(R.string.routing_rules_title)) {
                if (routing.hasIdleRules) {
                    Text(
                        stringResource(R.string.routing_rules_idle),
                        Modifier.padding(horizontal = 6.dp).testTag("routing.rules.idle"),
                        color = PgColor.Text.Muted,
                        style = PgType.Caption,
                    )
                }

                if (routing.rules.isEmpty()) {
                    EmptyNote(stringResource(R.string.routing_rules_empty), "routing.rules.empty")
                } else {
                    PgCard(Modifier.fillMaxWidth(), contentPadding = ListCard) {
                        routing.rules.forEachIndexed { index, rule ->
                            RuleRow(rule, routing.split, divider = index > 0) { editing = index }
                        }
                    }
                }

                DashedButton(stringResource(R.string.routing_rules_add), "routing.rules.add") { editing = NEW_RULE }
            }

            Section(stringResource(R.string.routing_always_title)) {
                PgCard(Modifier.fillMaxWidth(), contentPadding = ListCard) {
                    Column(Modifier.fillMaxWidth().padding(vertical = PgSpace.L), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(stringResource(R.string.routing_server_title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
                        Text(stringResource(R.string.routing_server_text), color = PgColor.Text.Muted, style = PgType.Caption)
                    }
                    LanRow(tunnel.allowLan, onProtection)
                }
            }
        }
    }

    val open = editing
    if (open != null) {
        val original = routing.rules.getOrNull(open)
        RuleSheet(
            original = original,
            split = routing.split,
            onSave = { rule ->
                editing = null
                change(routing.saveRule(original, rule))
            },
            onDelete = {
                editing = null
                original?.let { change(routing.removeRule(it)) }
            },
            onClose = { editing = null },
        )
    }

    if (picking) {
        // Подсказка «уже в списке» — из подсказок окна (LocalPgToast): лист к этому времени закрыт.
        val toast = LocalPgToast.current
        val context = LocalContext.current
        AppPicker(
            installedApps,
            onPick = { app ->
                picking = false
                // Уже в списке — включить, а не заводить вторую строку (как AddApp десктопа).
                val existing = routing.apps.firstOrNull { it.packageName == app.packageName }
                change(routing.addApp(app.packageName, app.label))
                if (existing != null) {
                    toast.show(context.getString(R.string.routing_apps_exists, existing.name))
                }
            },
            onClose = { picking = false },
        )
    }

    if (askingSplit) {
        PgDialog(
            stringResource(R.string.dialog_splitMode_title),
            stringResource(R.string.dialog_splitMode_text),
            PgIcons.Shield,
            PgTone.Caution,
            stringResource(R.string.dialog_splitMode_keep),
            { askingSplit = false },
            stringResource(R.string.dialog_splitMode_switch),
            {
                askingSplit = false
                change(routing.copy(mode = RoutingMode.Split))
            },
            { askingSplit = false },
            "dialog.split",
            detail = PgIcons.MarkAlert,
            secondaryDanger = true,
        )
    }
}

/** Номер «нового правила» в [RoutingScreen]: правил с таким номером не бывает. */
private const val NEW_RULE = -1

/** Поля карточек-списков доски 13 (Padding="16,2"): строки без боковых полей, полосы — внутри полей карточки. */
private val ListCard = PaddingValues(horizontal = PgSpace.Card, vertical = 2.dp)

/** Имя пакета под названием программы — моноширинным размера overline, как путь программы на доске. */
private val PackageText = PgType.Mono.copy(fontSize = PgType.Overline.fontSize, fontWeight = FontWeight.Normal)

/**
 * «Что идет через VPN» (карточка 16 × 14): заголовок, сегменты «Весь трафик · Раздельный режим» и подпись по режиму — со
 * словом «Android» в режиме «Весь трафик» (имя ОС, как AppInfo.OsName десктопа). Теги: сегменты — «routing.mode.*»,
 * подпись — «routing.mode.note».
 */
@Composable
private fun ModeCard(mode: RoutingMode, onMode: (RoutingMode) -> Unit) {
    PgCard(Modifier.fillMaxWidth(), contentPadding = PaddingValues(horizontal = PgSpace.Card, vertical = 14.dp)) {
        Text(stringResource(R.string.routing_mode_title), color = PgColor.Text.Primary, style = PgType.BodyStrong)
        Spacer(Modifier.height(PgSpace.M))
        PgSegments(
            RoutingMode.entries,
            mode,
            { stringResource(if (it == RoutingMode.Split) R.string.routing_mode_split else R.string.routing_mode_all) },
            "routing.mode",
            onMode,
        )
        Spacer(Modifier.height(PgSpace.M))
        val note = if (mode == RoutingMode.Split) {
            stringResource(R.string.routing_mode_split_note)
        } else {
            stringResource(R.string.routing_mode_all_note, OS_NAME)
        }
        Text(note, Modifier.testTag("routing.mode.note"), color = PgColor.Text.Muted, style = PgType.Caption)
    }
}

/** Имя ОС в подписи режима: название системы, не перевод. */
private const val OS_NAME = "Android"

/**
 * Предупреждение раздельного режима (Border.notice): status.warning.tint, рамка status.warning.line, скругление 16, значок
 * Warning и текст цветом status.warning.badge.
 */
@Composable
private fun SplitWarning() {
    val shape = RoundedCornerShape(16.dp)
    Row(
        Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(PgColor.Status.Warning.Tint)
            .border(1.dp, PgColor.Status.Warning.Line, shape)
            .padding(horizontal = 14.dp, vertical = PgSpace.L)
            .testTag("routing.warning"),
        horizontalArrangement = Arrangement.spacedBy(PgSpace.M),
    ) {
        PgIcon(PgIcons.Warning, null, Modifier.padding(top = 1.dp), color = PgColor.Status.Warning.Badge, size = 18.dp, strokeWidth = 2f)
        Text(stringResource(R.string.routing_warning), color = PgColor.Status.Warning.Badge, style = PgType.Small)
    }
}

/** Раздел страницы: надзаголовок на 6 правее края карточки и содержимое через 8. */
@Composable
private fun Section(title: String, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(PgSpace.S)) {
        Overline(title, Modifier.padding(horizontal = 6.dp))
        content()
    }
}

/** Пустой список — строкой t-small вместо карточки (как AppsEmpty и RulesEmpty десктопа). */
@Composable
private fun EmptyNote(text: String, tag: String) {
    Text(text, Modifier.padding(horizontal = 6.dp).testTag(tag), color = PgColor.Text.Secondary, style = PgType.Small)
}

/**
 * Программа мимо VPN: плитка с буквой, название и имя пакета, корзина «Убрать …» и переключатель — нажимается вся строка.
 * [onChange] получает программу с новым флагом или null — убрать из списка. Теги: строка — «routing.app.<пакет>», корзина —
 * «routing.app.<пакет>.remove».
 */
@Composable
private fun AppRow(app: BypassApp, index: Int, divider: Boolean, onChange: (BypassApp?) -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val switchLabel = stringResource(R.string.routing_apps_switch, app.name)
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .toggleable(
                value = app.enabled,
                interactionSource = interaction,
                indication = null,
                role = Role.Switch,
                onValueChange = { onChange(app.copy(enabled = it)) },
            )
            .semantics { contentDescription = switchLabel }
            .padding(vertical = 11.dp)
            .testTag("routing.app.${app.packageName}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        AppTile(app.name, index)
        AppNames(app.name, app.packageName, Modifier.weight(1f).padding(start = PgSpace.L, end = PgSpace.S))
        RemoveButton(stringResource(R.string.routing_apps_remove, app.name), "routing.app.${app.packageName}.remove") { onChange(null) }
        Spacer(Modifier.width(6.dp))
        PgSwitch(app.enabled)
    }
}

/** Название программы полужирным и имя пакета под ним — в одну строку каждое, длинное — с многоточием. */
@Composable
private fun AppNames(name: String, packageName: String, modifier: Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(name, color = PgColor.Text.Primary, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(packageName, color = PgColor.Text.Muted, style = PackageText, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Корзина строки (.btn-icon 36): серый значок 17, при нажатии — синий и сжатие 0,98 без заливки (решение 485). */
@Composable
private fun RemoveButton(label: String, tag: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Box(
        Modifier
            .size(36.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(RoundedCornerShape(PgRadius.Button))
            .clickable(interactionSource = interaction, indication = null, onClickLabel = label, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label }
            .testTag(tag),
        contentAlignment = Alignment.Center,
    ) {
        PgIcon(PgIcons.Trash, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Muted, size = 17.dp, strokeWidth = 1.9f)
    }
}

/**
 * Правило для сайта (.btn-plain строки): шаблон моноширинным и метка действия; нажатие открывает лист. «Напрямую» вне
 * раздельного режима не действует — метка бледнее. Тег — «routing.rule.<шаблон>», метки — «routing.rule.<шаблон>.action».
 */
@Composable
private fun RuleRow(rule: SiteRule, split: Boolean, divider: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(vertical = PgSpace.L)
            .testTag("routing.rule.${rule.pattern}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            rule.pattern,
            Modifier.weight(1f).padding(end = PgSpace.M),
            color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary,
            style = PgType.Mono,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        ActionPill(rule.action, idle = rule.action == RuleAction.Direct && !split, tag = "routing.rule.${rule.pattern}.action")
    }
}

/**
 * Метка действия (Border.action, Padding="10,4", круглая): «Напрямую» — оранжевая, «Через VPN» — зеленая, «Блокировать» —
 * красная; [idle] — прозрачность 0,55.
 */
@Composable
private fun ActionPill(action: RuleAction, idle: Boolean, tag: String) {
    val (fill, ink) = when (action) {
        RuleAction.Direct -> PgColor.Status.Warning.Tint to PgColor.Status.Warning.Badge
        RuleAction.Proxy -> PgColor.Status.Success.Tint to PgColor.Status.Success.Badge
        RuleAction.Block -> PgColor.Status.Danger.Tint to PgColor.Status.Danger.Badge
    }
    Text(
        stringResource(actionLabel(action)),
        Modifier
            .alpha(if (idle) 0.55f else 1f)
            .clip(CircleShape)
            .background(fill)
            .padding(horizontal = PgSpace.M, vertical = PgSpace.Xxs)
            .testTag(tag),
        color = ink,
        style = PgType.Caption.copy(fontWeight = FontWeight.SemiBold),
        maxLines = 1,
    )
}

@StringRes
private fun actionLabel(action: RuleAction): Int = when (action) {
    RuleAction.Direct -> R.string.routing_action_direct
    RuleAction.Proxy -> R.string.routing_action_proxy
    RuleAction.Block -> R.string.routing_action_block
}

/**
 * «Локальная сеть» (.btn-plain со стрелкой): заголовок и состояние — «Сейчас запрещена/разрешена · настраивается в «Защите и
 * утечках»», серая стрелка; при нажатии заголовок и стрелка синеют. Ведет в «Защиту и утечки»: локальная сеть настраивается
 * на одном экране. Тег — «routing.lan», состояние — «routing.lan.state».
 */
@Composable
private fun LanRow(allowLan: Boolean, onProtection: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(true)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onProtection)
            .padding(vertical = PgSpace.L)
            .testTag("routing.lan"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f).padding(end = PgSpace.M), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(stringResource(R.string.routing_lan_title), color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, style = PgType.BodyStrong)
            Text(
                stringResource(if (allowLan) R.string.routing_lan_allowed else R.string.routing_lan_denied),
                Modifier.testTag("routing.lan.state"),
                color = PgColor.Text.Muted,
                style = PgType.Caption,
            )
        }

        PgIcon(PgIcons.ChevronRight, null, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Muted, size = 16.dp)
    }
}

/**
 * «Добавить …» (.btn-plain.dashed): высота 46, пунктирная рамка accent.line на accent.tint, «+» и текст accent.text; при
 * нажатии рамка и текст accent.base, сжатие 0,98.
 */
@Composable
private fun DashedButton(label: String, tag: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val edge: Color = if (pressed) PgColor.Accent.Base else PgColor.Accent.Line
    val ink = if (pressed) PgColor.Accent.Base else PgColor.Accent.Text
    val shape = RoundedCornerShape(14.dp)
    Row(
        Modifier
            .fillMaxWidth()
            .height(46.dp)
            .scale(if (pressed) 0.98f else 1f)
            .clip(shape)
            .background(PgColor.Accent.Tint)
            .drawBehind {
                val stroke = 1.5.dp.toPx()
                drawRoundRect(
                    color = edge,
                    topLeft = Offset(stroke / 2, stroke / 2),
                    size = Size(size.width - stroke, size.height - stroke),
                    cornerRadius = CornerRadius(14.dp.toPx() - stroke / 2),
                    style = Stroke(width = stroke, pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx()))),
                )
            }
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .testTag(tag),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(PgSpace.S, Alignment.CenterHorizontally),
    ) {
        PgIcon(PgIcons.Plus, null, color = ink, size = 18.dp, strokeWidth = 2.2f)
        Text(label, color = ink, style = PgType.BodyStrong)
    }
}

/**
 * Лист «Новое правило» / «Правило для сайта» (SiteRuleEditView десктопа): поле «Сайт или адрес» моноширинным (до 253 знаков)
 * с подсказкой, сегменты «Напрямую · Через VPN · Блокировать», под ними — что «Напрямую» в режиме «Весь трафик» пока не
 * действует; внизу «Отмена» и «Добавить» или «Удалить» (красная) и «Сохранить». Новое правило — «Напрямую» в раздельном
 * режиме, «Блокировать» в режиме «Весь трафик». Шаблон, который телефон не берет (и geosite), не сохраняется — лист
 * говорит, какой нужен; подсказка и ошибка под полем geosite не предлагают (как у iOS).
 */
@Composable
private fun RuleSheet(original: SiteRule?, split: Boolean, onSave: (SiteRule) -> Unit, onDelete: () -> Unit, onClose: () -> Unit) {
    var text by rememberSaveable { mutableStateOf(original?.pattern.orEmpty()) }
    var action by rememberSaveable { mutableStateOf(original?.action ?: if (split) RuleAction.Direct else RuleAction.Block) }
    var bad by rememberSaveable { mutableStateOf(false) }
    val save: () -> Unit = {
        val pattern = SitePattern.normalize(text)
        if (pattern == null) {
            bad = true
        } else {
            onSave(SiteRule(pattern, action))
        }
    }
    PgSheet(onClose, "routing.rule.sheet", scroll = false) {
        SheetHeader(stringResource(if (original == null) R.string.routing_rule_new else R.string.routing_rule_edit), onClose, "routing.rule.close")
        Column(
            Modifier
                .fillMaxWidth()
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(vertical = PgSpace.Card),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                val label = stringResource(R.string.routing_rule_pattern)
                FieldLabel(label)
                PgInput(
                    text,
                    {
                        if (it.length <= SitePattern.MAX_LENGTH) {
                            text = it
                            bad = false
                        }
                    },
                    label,
                    "routing.rule.pattern",
                    placeholder = stringResource(R.string.routing_rule_placeholder),
                    style = MonoField,
                    keyboard = KeyboardOptions(keyboardType = KeyboardType.Uri),
                )
                if (bad) {
                    Text(withoutGeosite(stringResource(R.string.routing_rule_bad)), Modifier.testTag("routing.rule.bad"), color = PgColor.Status.Danger.Text, style = PgType.Caption)
                } else {
                    Text(withoutGeosite(stringResource(R.string.routing_rule_hint)), Modifier.testTag("routing.rule.hint"), color = PgColor.Text.Muted, style = PgType.Caption)
                }
            }

            Column(verticalArrangement = Arrangement.spacedBy(PgSpace.Xs)) {
                FieldLabel(stringResource(R.string.routing_rule_action))
                PgSegments(RuleAction.entries, action, { stringResource(actionLabel(it)) }, "routing.rule.action", { action = it })
                if (action == RuleAction.Direct && !split) {
                    Text(stringResource(R.string.routing_rules_idle), Modifier.testTag("routing.rule.idle"), color = PgColor.Text.Muted, style = PgType.Caption)
                }
            }
        }

        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(PgSpace.S)) {
            if (original == null) {
                PgSecondaryButton(stringResource(R.string.dialog_cancel), "routing.rule.cancel", onClose, Modifier.weight(1f), height = 50.dp)
                PgPrimaryButton(stringResource(R.string.routing_rule_add), "routing.rule.save", save, Modifier.weight(1f), height = 50.dp)
            } else {
                PgSecondaryButton(stringResource(R.string.routing_rule_delete), "routing.rule.delete", onDelete, Modifier.weight(1f), danger = true, height = 50.dp)
                PgPrimaryButton(stringResource(R.string.routing_rule_save), "routing.rule.save", save, Modifier.weight(1f), height = 50.dp)
            }
        }
    }
}

/**
 * Подсказка или ошибка под полем шаблона без пункта «geosite:категория»: базы geosite у ядра телефона нет, и такой шаблон
 * не принимается (`SitePattern`), а тексты десктопа его предлагают. Оба текста на обоих языках — перечень через «, ».
 */
internal fun withoutGeosite(text: String): String = text.split(", ").filterNot { it.contains("geosite:") }.joinToString(", ")

/**
 * Лист «Добавить программу»: программы телефона со значком запуска (PhoneApps), по названию; нажатие добавляет программу и
 * закрывает лист. Список читается, когда лист открыт. Теги: лист — «routing.apps.sheet», строка — «routing.apps.pick.<пакет>».
 */
@Composable
private fun AppPicker(installedApps: suspend () -> List<PhoneApp>, onPick: (PhoneApp) -> Unit, onClose: () -> Unit) {
    var apps by remember { mutableStateOf(emptyList<PhoneApp>()) }
    LaunchedEffect(Unit) { apps = installedApps() }
    PgSheet(onClose, "routing.apps.sheet", scroll = false) {
        SheetHeader(stringResource(R.string.routing_apps_add), onClose, "routing.apps.close")
        LazyColumn(Modifier.fillMaxWidth().weight(1f).padding(top = PgSpace.S)) {
            itemsIndexed(apps, key = { _, app -> app.packageName }) { index, app ->
                PickRow(app, index, divider = index > 0) { onPick(app) }
            }
        }
    }
}

/** Программа в листе выбора: плитка с буквой, название и имя пакета; при нажатии название синеет. */
@Composable
private fun PickRow(app: PhoneApp, index: Int, divider: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    Row(
        Modifier
            .fillMaxWidth()
            .pgTopDivider(divider)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(vertical = 11.dp)
            .testTag("routing.apps.pick.${app.packageName}"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        AppTile(app.label, index)
        Column(Modifier.weight(1f).padding(start = PgSpace.L), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(app.label, color = if (pressed) PgColor.Accent.Base else PgColor.Text.Primary, style = PgType.BodyStrong, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(app.packageName, color = PgColor.Text.Muted, style = PackageText, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}
