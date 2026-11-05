import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Маршрутизация» для телефона (доска 13, RoutingPage десктопа): шапка с «Назад» стоит на месте, прокручиваются карточка
/// «Что идет через VPN» (сегменты «Весь трафик» / «Раздельный режим» и подпись по режиму; переключение без вопроса, решение
/// 111), в раздельном режиме — предупреждение, «Правила для сайтов» (строки с меткой действия, «Напрямую» вне раздельного
/// режима бледнее и над списком — подпись, что такие правила не действуют; нажатие и «Добавить правило» открывают нижний
/// лист) и «Всегда напрямую» (адрес сервера, «Локальная сеть» ведет в «Защиту и утечки»). Блока «Программы мимо VPN» у iOS
/// нет: обычному приложению выбирать, какие программы идут мимо туннеля, iOS не дает (только профилям MDM). Правила идут в
/// конфиг Xray расширения туннеля; под защитой изменения применяются сразу — ядро перезапускается в том же туннеле.
struct RoutingView: View {
    let texts: PgTexts
    let routing: RoutingSettings
    /// Разрешена ли локальная сеть («Защита и утечки») — подпись строки «Локальная сеть».
    let allowLan: Bool
    let onRouting: (RoutingSettings) -> Void
    /// «Локальная сеть» настраивается на одном экране — «Защита и утечки».
    let onLan: () -> Void
    let onBack: () -> Void

    /// Правило в листе (nil — новое) и открыт ли лист: правило остается, пока лист уезжает вниз, — лист не пустеет на ходу.
    @State private var editing: SiteRule?
    @State private var sheetOpen: Bool
    /// Номер открытия листа: каждый раз лист начинается заново, с полями своего правила.
    @State private var opened = 0

    init(
        texts: PgTexts, routing: RoutingSettings, allowLan: Bool, editing: SiteRule? = nil, sheetOpen: Bool = false,
        onRouting: @escaping (RoutingSettings) -> Void, onLan: @escaping () -> Void, onBack: @escaping () -> Void
    ) {
        self.texts = texts
        self.routing = routing
        self.allowLan = allowLan
        self.onRouting = onRouting
        self.onLan = onLan
        self.onBack = onBack
        _editing = State(initialValue: editing)
        _sheetOpen = State(initialValue: sheetOpen)
    }

    var body: some View {
        VStack(spacing: 0) {
            PgPageHeader(title: texts.settingsAdvRoutingTitle, texts: texts, fixed: true, onBack: onBack)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    modeCard
                    if routing.isSplit {
                        RoutingWarning(text: texts.routingWarning)
                    }

                    rulesSection
                    alwaysSection
                }
                .padding(.horizontal, PgSpace.windowSideMobile)
                .padding(.bottom, PgLayout.scrollBottomPadding)
            }
        }
        .pgSheet(isPresented: $sheetOpen) {
            SiteRuleSheet(
                texts: texts, original: editing, split: routing.isSplit,
                onSave: { rule in
                    sheetOpen = false
                    onRouting(routing.saving(rule, replacing: editing))
                },
                onDelete: { rule in
                    sheetOpen = false
                    onRouting(routing.removing(rule))
                },
                onClose: { sheetOpen = false }
            )
            .id(opened)
        }
        .accessibilityIdentifier("routing")
    }

    /// «Что идет через VPN» (.card с сегментами): заголовок, сегменты и подпись по режиму.
    private var modeCard: some View {
        VStack(alignment: .leading, spacing: PgSpace.m) {
            Text(texts.routingModeTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
            PgSegments(
                options: RoutingLook.modeOptions(texts), selection: routing.mode, identifier: "routing.mode",
                confirm: { RoutingLook.modeDialog($0, texts) },
                onSelect: { mode in
                    var next = routing
                    next.mode = mode
                    onRouting(next)
                }
            )
            Text(RoutingLook.modeNote(routing.mode, texts))
                .pgText(PgType.caption)
                .foregroundStyle(Color(PgColor.Text.muted))
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityIdentifier("routing.mode.note")
        }
        .padding(.vertical, 14)
        .padding(.horizontal, PgSpace.card)
        .pgCard(padding: 0)
    }

    /// «Правила для сайтов»: подпись о молчащих «Напрямую», карточка строк (пусто — строка «Правил нет») и «Добавить правило».
    private var rulesSection: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: texts.routingRulesTitle)
                .padding(.horizontal, PgSpace.xs)
                .accessibilityAddTraits(.isHeader)
            if routing.hasIdleRules {
                Text(texts.routingRulesIdle)
                    .pgText(PgType.caption)
                    .foregroundStyle(Color(PgColor.Text.muted))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, PgSpace.xs)
                    .accessibilityIdentifier("routing.idle")
            }

            if routing.rules.isEmpty {
                Text(texts.routingRulesEmpty)
                    .pgText(PgType.small)
                    .foregroundStyle(Color(PgColor.Text.secondary))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, PgSpace.xs)
                    .accessibilityIdentifier("routing.empty")
            } else {
                VStack(spacing: 0) {
                    ForEach(routing.rules.indices, id: \.self) { index in
                        let rule = routing.rules[index]
                        if index > 0 {
                            PgDivider()
                        }

                        RuleRow(texts: texts, rule: rule, idle: RoutingLook.isIdle(rule.action, split: routing.isSplit)) {
                            open(rule)
                        }
                    }
                }
                .padding(.horizontal, PgSpace.card)
                .padding(.vertical, 2)
                .pgCard(padding: 0)
            }

            AddRuleButton(title: texts.routingRulesAdd) {
                open(nil)
            }
        }
    }

    /// «Всегда напрямую»: адрес выбранного сервера (строка без действия) и «Локальная сеть» со стрелкой — в «Защиту и утечки».
    private var alwaysSection: some View {
        VStack(alignment: .leading, spacing: PgSpace.s) {
            Overline(text: texts.routingAlwaysTitle)
                .padding(.horizontal, PgSpace.xs)
                .accessibilityAddTraits(.isHeader)
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(texts.routingServerTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                    Text(texts.routingServerText)
                        .pgText(PgType.caption)
                        .foregroundStyle(Color(PgColor.Text.muted))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.vertical, PgSpace.l)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityElement(children: .combine)

                PgDivider()
                Button(action: onLan) {
                    HStack(spacing: PgSpace.m) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(texts.routingLanTitle).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                            Text(RoutingLook.lanText(allowLan, texts))
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Text.muted))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)

                        PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 16, lineWidth: 2.2)
                    }
                    .padding(.vertical, PgSpace.l)
                    .frame(minHeight: PgLayout.touchTarget)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityElement(children: .combine)
                .accessibilityAddTraits(.isButton)
                .accessibilityIdentifier("routing.lan")
            }
            .padding(.horizontal, PgSpace.card)
            .padding(.vertical, 2)
            .pgCard(padding: 0)
        }
    }

    /// Открыть лист: правило — «Правило для сайта», nil — «Новое правило».
    private func open(_ rule: SiteRule?) {
        editing = rule
        opened += 1
        sheetOpen = true
    }
}

/// Правила «Маршрутизации» — как RoutingPageViewModel и SiteRuleEditViewModel десктопа.
enum RoutingLook {
    /// Имя ОС в подписи «Весь трафик … идет через VPN» (data-os-word="name" макета, AppInfo.OsName десктопа).
    static let osName = "iOS"

    static func modeOptions(_ texts: PgTexts) -> [PgOption<RoutingMode>] {
        [PgOption(value: RoutingMode.allTraffic, title: texts.routingModeAll), PgOption(value: RoutingMode.split, title: texts.routingModeSplit)]
    }

    /// Вопрос перед «Раздельным режимом» (просьба пользователя 2026-11-05, ConfirmSplitModeAsync десктопа): часть трафика уходит
    /// мимо туннеля и блока при обрыве. Основная кнопка — «Оставить весь трафик»; на «Весь трафик» — без вопроса.
    static func modeDialog(_ mode: RoutingMode, _ texts: PgTexts) -> PgDialog? {
        guard mode == .split else { return nil }
        return PgDialog(
            kind: .warning, icon: PgIcons.shield, detail: PgIcons.markAlert, title: texts.dialogSplitModeTitle, text: texts.dialogSplitModeText,
            primary: texts.dialogSplitModeKeep, secondary: texts.dialogSplitModeSwitch, secondaryDanger: true
        )
    }

    /// Подпись под сегментами: «Весь трафик iOS идет через VPN…» или «VPN для всего, кроме…».
    static func modeNote(_ mode: RoutingMode, _ texts: PgTexts) -> String {
        mode == .split ? texts.routingModeSplitNote : texts.routingModeAllNote(osName)
    }

    /// Действия правила сегментами по порядку доски.
    static func actionOptions(_ texts: PgTexts) -> [PgOption<RuleAction>] {
        RuleAction.allCases.map { PgOption(value: $0, title: actionTitle($0, texts)) }
    }

    static func actionTitle(_ action: RuleAction, _ texts: PgTexts) -> String {
        switch action {
        case .direct: return texts.routingActionDirect
        case .proxy: return texts.routingActionProxy
        case .block: return texts.routingActionBlock
        }
    }

    /// Метка действия (.pill доски): «Напрямую» — оранжевая (warning), «Через VPN» — зеленая (success), «Блокировать» —
    /// красная (danger): текст badge на оттенке tint.
    static func pillColors(_ action: RuleAction) -> (ink: PgRGBA, tint: PgRGBA) {
        switch action {
        case .direct: return (PgColor.Status.Warning.badge, PgColor.Status.Warning.tint)
        case .proxy: return (PgColor.Status.Success.badge, PgColor.Status.Success.tint)
        case .block: return (PgColor.Status.Danger.badge, PgColor.Status.Danger.tint)
        }
    }

    /// Новое правило в раздельном режиме — «Напрямую» (ради этого его и заводят), в режиме «Весь трафик» — «Блокировать»:
    /// «Напрямую» там не действует.
    static func newAction(split: Bool) -> RuleAction {
        split ? .direct : .block
    }

    /// «Напрямую» вне раздельного режима не действует: метка бледнее, в листе — подпись об этом.
    static func isIdle(_ action: RuleAction, split: Bool) -> Bool {
        action == .direct && !split
    }

    /// Подпись «Локальной сети»: «Сейчас запрещена · …» или «Сейчас разрешена · …».
    static func lanText(_ allowLan: Bool, _ texts: PgTexts) -> String {
        allowLan ? texts.routingLanAllowed : texts.routingLanDenied
    }

    /// Подсказка под полем шаблона — без «geosite:категория»: базы geosite у ядра телефона нет, такой шаблон не принимается
    /// (`SitePattern`), а текст десктопа его предлагает.
    static func patternHint(_ texts: PgTexts) -> String {
        withoutGeosite(texts.routingRuleHint)
    }

    /// Ошибка под полем шаблона — тоже без «geosite:категория».
    static func patternBad(_ texts: PgTexts) -> String {
        withoutGeosite(texts.routingRuleBad)
    }

    /// Перечень через «, » без пункта с «geosite:» — так устроены подсказка и ошибка на обоих языках.
    static func withoutGeosite(_ text: String) -> String {
        text.components(separatedBy: ", ").filter { !$0.contains("geosite:") }.joined(separator: ", ")
    }
}

/// Предупреждение раздельного режима (.banner.warning доски 13, Border.notice десктопа): значок Warning 18 и текст small
/// цветом status.warning.badge на status.warning.tint, рамка status.warning.line, скругление 16, поля 14 × 12.
private struct RoutingWarning: View {
    let text: String

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        HStack(alignment: .top, spacing: PgSpace.m) {
            PgIcon(data: PgIcons.warning, color: Color(PgColor.Status.Warning.badge), size: 18, lineWidth: 2)
                .padding(.top, 1)
            Text(text)
                .pgText(PgType.small)
                .foregroundStyle(Color(PgColor.Status.Warning.badge))
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.vertical, PgSpace.l)
        .padding(.horizontal, 14)
        .background(shape.fill(Color(PgColor.Status.Warning.tint)))
        .overlay(shape.strokeBorder(Color(PgColor.Status.Warning.line), lineWidth: 1))
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("routing.warning")
    }
}

/// Строка правила (Border.row десктопа): шаблон моноширинным в одну строку и метка действия справа; нажатие открывает лист.
private struct RuleRow: View {
    let texts: PgTexts
    let rule: SiteRule
    let idle: Bool
    let action: () -> Void

    var body: some View {
        let title = RoutingLook.actionTitle(rule.action, texts)
        Button(action: action) {
            HStack(spacing: 0) {
                Text(rule.pattern)
                    .pgText(PgType.mono)
                    .foregroundStyle(Color(PgColor.Text.primary))
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.trailing, PgSpace.m)
                ActionPill(text: title, action: rule.action, idle: idle)
            }
            .padding(.vertical, PgSpace.l)
            .frame(minHeight: PgLayout.touchTarget)
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .accessibilityLabel(texts.routingRuleEditLabel(rule.pattern, title))
        .accessibilityIdentifier("routing.rule")
    }
}

/// Метка действия (.pill, Border.action десктопа): caption полужирным, поля 10 × 4, скругление во всю высоту; «Напрямую» вне
/// раздельного режима — прозрачность 0,55.
private struct ActionPill: View {
    let text: String
    let action: RuleAction
    let idle: Bool

    var body: some View {
        let colors = RoutingLook.pillColors(action)
        Text(text)
            .pgText(PgType.caption.with(weight: PgType.bodyStrong.weight))
            .foregroundStyle(Color(colors.ink))
            .lineLimit(1)
            .padding(.horizontal, PgSpace.m)
            .padding(.vertical, PgSpace.xxs)
            .background(Capsule().fill(Color(colors.tint)))
            .opacity(idle ? 0.55 : 1)
            .fixedSize()
    }
}

/// «Добавить правило» (.btn-plain.dashed): пунктирная рамка accent.line на accent.tint, «+» и подпись accent.text полужирным.
private struct AddRuleButton: View {
    let title: String
    let action: () -> Void

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        Button(action: action) {
            HStack(spacing: PgSpace.s) {
                PgIcon(data: PgIcons.plus, color: Color(PgColor.Accent.text), size: 18, lineWidth: 2.2)
                Text(title).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Accent.text)).lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: 46)
            .background(shape.fill(Color(PgColor.Accent.tint)))
            .overlay(shape.strokeBorder(Color(PgColor.Accent.line), style: StrokeStyle(lineWidth: 1.5, dash: [5, 4])))
            .contentShape(Rectangle())
        }
        .buttonStyle(PgPressStyle())
        .accessibilityIdentifier("routing.add")
    }
}

/// Лист правила для сайта (.sheet доски 13, SiteRuleEditView десктопа): заголовок «Новое правило» или «Правило для сайта» и
/// крестик; поле «Сайт или адрес» моноширинным до 253 знаков с подсказкой (неверный шаблон — под полем красным, почему);
/// «Что делать» — сегменты «Напрямую / Через VPN / Блокировать», при «Напрямую» в режиме «Весь трафик» — подпись, что такое
/// правило пока не действует; внизу «Отмена» и «Добавить» или красная «Удалить» и «Сохранить». Шаблон geosite телефон не
/// принимает: базы geosite у ядра телефона нет (`SitePattern`), поэтому подсказка и ошибка под полем его не предлагают.
struct SiteRuleSheet: View {
    let texts: PgTexts
    /// Правило, которое правят; nil — новое.
    let original: SiteRule?
    let split: Bool
    let onSave: (SiteRule) -> Void
    let onDelete: (SiteRule) -> Void
    let onClose: () -> Void

    @State private var pattern: String
    @State private var choice: RuleAction
    @State private var tried: Bool

    init(
        texts: PgTexts, original: SiteRule?, split: Bool, tried: Bool = false, onSave: @escaping (SiteRule) -> Void,
        onDelete: @escaping (SiteRule) -> Void, onClose: @escaping () -> Void
    ) {
        self.texts = texts
        self.original = original
        self.split = split
        self.onSave = onSave
        self.onDelete = onDelete
        self.onClose = onClose
        _pattern = State(initialValue: original?.pattern ?? "")
        _choice = State(initialValue: original?.action ?? RoutingLook.newAction(split: split))
        _tried = State(initialValue: tried)
    }

    var body: some View {
        let bad = tried && SitePattern.normalize(pattern) == nil
        VStack(spacing: 0) {
            PgSheetHeader(title: original == nil ? texts.routingRuleNew : texts.routingRuleEdit, closeLabel: texts.cardClose, identifier: "rule.close", onClose: onClose)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    VStack(alignment: .leading, spacing: PgSpace.xs) {
                        label(texts.routingRulePattern)
                        PgField(
                            placeholder: texts.routingRulePlaceholder, text: $pattern,
                            style: PgTextStyle(size: PgType.caption.size, weight: PgType.mono.weight, mono: true), identifier: "rule.pattern"
                        )
                        .keyboardType(.URL)
                        .accessibilityLabel(texts.routingRulePattern)
                        .overlay {
                            if bad {
                                RoundedRectangle(cornerRadius: PgRadius.input, style: .continuous).stroke(Color(PgColor.Status.Danger.base), lineWidth: 1)
                            }
                        }
                        if bad {
                            Text(RoutingLook.patternBad(texts))
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Status.Danger.text))
                                .fixedSize(horizontal: false, vertical: true)
                                .accessibilityIdentifier("rule.bad")
                        } else {
                            Text(RoutingLook.patternHint(texts))
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Text.muted))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                    }

                    VStack(alignment: .leading, spacing: PgSpace.xs) {
                        label(texts.routingRuleAction)
                        PgSegments(options: RoutingLook.actionOptions(texts), selection: choice, identifier: "rule.action", onSelect: { choice = $0 })
                        if RoutingLook.isIdle(choice, split: split) {
                            Text(texts.routingRulesIdle)
                                .pgText(PgType.caption)
                                .foregroundStyle(Color(PgColor.Text.muted))
                                .fixedSize(horizontal: false, vertical: true)
                                .accessibilityIdentifier("rule.idle")
                        }
                    }
                }
                .padding(.vertical, PgSpace.card)
            }

            HStack(spacing: PgSpace.s) {
                if let original {
                    PgActionButton(label: texts.routingRuleDelete, kind: .secondary, danger: true, identifier: "rule.delete", height: 50) {
                        onDelete(original)
                    }
                    PgActionButton(label: texts.routingRuleSave, identifier: "rule.save", height: 50) {
                        save()
                    }
                } else {
                    PgActionButton(label: texts.dialogCancel, kind: .secondary, identifier: "rule.cancel", height: 50, action: onClose)
                    PgActionButton(label: texts.routingRuleAdd, identifier: "rule.add", height: 50) {
                        save()
                    }
                }
            }
        }
        .onChange(of: pattern) { next in
            tried = false
            if next.count > SitePattern.maxLength {
                pattern = String(next.prefix(SitePattern.maxLength))
            }
        }
        .accessibilityIdentifier("rule.sheet")
    }

    private func label(_ text: String) -> some View {
        Text(text).pgText(PgType.small.with(weight: PgType.bodyStrong.weight)).foregroundStyle(Color(PgColor.Text.secondary))
    }

    /// «Добавить» и «Сохранить»: шаблон — в обычном виде (`SitePattern.normalize`); неверный — подсказка под полем, лист
    /// остается.
    private func save() {
        tried = true
        if let normalized = SitePattern.normalize(pattern) {
            onSave(SiteRule(pattern: normalized, action: choice))
        }
    }
}
