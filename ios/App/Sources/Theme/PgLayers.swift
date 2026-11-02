import Foundation
import PrivateGatewayTheme
import SwiftUI
import UIKit

/// Пункт меню у кнопки (.popup): подпись; текущий — синим полужирным без заливки (.btn-option.current), опасное действие —
/// красным.
struct PgMenuItem: Equatable {
    let title: String
    var current = false
    var danger = false
}

/// Открытое меню: у какой кнопки оно стоит (`pgMenuAnchor`), пункты и что делать с выбранным.
struct PgMenu {
    let anchor: UUID
    let items: [PgMenuItem]
    let onPick: (Int) -> Void
}

/// Слои поверх окна (app.css «Слой поверх окна», OverlayViewModel и ToastViewModel десктопа): диалог подтверждения с
/// затемнением, меню у кнопки и подсказка сверху. Один на окно — его ставит корень (`pgLayers`), блоки и экраны находят его
/// в окружении (`\.pgLayers`). У нижнего листа — свой, чтобы диалог из листа вставал поверх листа.
@MainActor
final class PgLayers: ObservableObject {
    @Published private(set) var dialog: PgDialog?
    @Published private(set) var menu: PgMenu?
    @Published private(set) var toast: String?

    private var onAnswer: ((PgDialogChoice) -> Void)?
    private var toastTimer: Task<Void, Never>?
    /// Номер показа подсказки: срок прежней не убирает новую.
    private var toastCount = 0

    init() {}

    /// Показать диалог; ответ — один раз: кнопка или затемнение (`.dismissed`). Новый вопрос закрывает прежний без ответа.
    func ask(_ dialog: PgDialog, then onAnswer: @escaping (PgDialogChoice) -> Void) {
        let previous = self.onAnswer
        self.onAnswer = onAnswer
        menu = nil
        self.dialog = dialog
        previous?(.dismissed)
    }

    /// Действие — только на «да» (`PgDialog.yes`); другая кнопка и затемнение ничего не делают.
    func confirm(_ dialog: PgDialog, then action: @escaping () -> Void) {
        ask(dialog) { choice in
            if choice == dialog.yes {
                action()
            }
        }
    }

    /// Ответ на открытый диалог: диалог закрывается, затем выполняется ответ.
    func answer(_ choice: PgDialogChoice) {
        guard let onAnswer else {
            return
        }

        self.onAnswer = nil
        dialog = nil
        onAnswer(choice)
    }

    /// Переход с вопросом (data-confirm макета): вопроса нет — сразу; есть — сначала диалог, действие — только на «да». Без
    /// слоев окна (блок нарисован вне корня) спросить негде — опасный переход не делается.
    static func confirming(_ dialog: PgDialog?, in layers: PgLayers?, _ action: @escaping () -> Void) {
        guard let dialog else {
            action()
            return
        }

        layers?.confirm(dialog, then: action)
    }

    /// Открыть меню у кнопки; открытое прежнее закрывается.
    func openMenu(_ menu: PgMenu) {
        self.menu = menu
    }

    /// Нажатие мимо меню или переход на другую страницу.
    func closeMenu() {
        menu = nil
    }

    /// Выбран пункт меню: меню закрывается, затем выполняется выбор.
    func pick(_ index: Int) {
        guard let menu, menu.items.indices.contains(index) else {
            return
        }

        self.menu = nil
        menu.onPick(index)
    }

    /// Подсказка сверху окна на `duration` секунд; новая сразу заменяет прежнюю. VoiceOver ее зачитывает (role="status" макета).
    func show(_ text: String, for duration: TimeInterval = PgToastDuration.action) {
        toastCount += 1
        let count = toastCount
        toastTimer?.cancel()
        toast = text
        UIAccessibility.post(notification: .announcement, argument: text)
        toastTimer = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(max(0, duration) * 1_000_000_000))
            self?.expire(count)
        }
    }

    /// Убрать подсказку раньше срока.
    func hideToast() {
        toastCount += 1
        toastTimer?.cancel()
        toastTimer = nil
        toast = nil
    }

    private func expire(_ count: Int) {
        guard count == toastCount else {
            return
        }

        toastTimer = nil
        toast = nil
    }
}

private struct PgLayersKey: EnvironmentKey {
    static let defaultValue: PgLayers? = nil
}

extension EnvironmentValues {
    /// Слои окна (или нижнего листа), в котором нарисован блок; вне корня — nil.
    var pgLayers: PgLayers? {
        get { self[PgLayersKey.self] }
        set { self[PgLayersKey.self] = newValue }
    }
}

/// Где стоят кнопки, у которых открывается меню: место каждой — в координатах слоев окна.
struct PgMenuAnchors: PreferenceKey {
    static var defaultValue: [UUID: Anchor<CGRect>] {
        [:]
    }

    static func reduce(value: inout [UUID: Anchor<CGRect>], nextValue: () -> [UUID: Anchor<CGRect>]) {
        value.merge(nextValue()) { _, next in next }
    }
}

extension View {
    /// Слои поверх содержимого: меню у кнопки, диалог с затемнением и подсказка сверху; блоки внутри находят их через
    /// окружение.
    func pgLayers(_ layers: PgLayers) -> some View {
        environment(\.pgLayers, layers)
            .overlayPreferenceValue(PgMenuAnchors.self) { anchors in
                PgLayersOverlay(layers: layers, anchors: anchors)
            }
    }

    /// Кнопка, у которой открывается меню `key` (`PgMenu.anchor`).
    func pgMenuAnchor(_ key: UUID) -> some View {
        anchorPreference(key: PgMenuAnchors.self, value: .bounds) { [key: $0] }
    }
}

/// Слои над окном: меню у своей кнопки, диалог посередине на затемнении overlay.scrim и подсказка сверху.
struct PgLayersOverlay: View {
    @ObservedObject var layers: PgLayers
    let anchors: [UUID: Anchor<CGRect>]

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                if let menu = layers.menu, let anchor = anchors[menu.anchor] {
                    menuLayer(menu, at: proxy[anchor], in: proxy.size)
                }

                if let dialog = layers.dialog {
                    Color(PgColor.Overlay.scrim)
                        .ignoresSafeArea()
                        .onTapGesture { layers.answer(.dismissed) }
                        .accessibilityHidden(true)
                    PgDialogView(dialog: dialog) { choice in
                        layers.answer(choice)
                    }
                    .accessibilityAction(.escape) { layers.answer(.dismissed) }
                }

                if let toast = layers.toast {
                    PgToastView(text: toast)
                        .padding(.top, PgToastView.top)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                        .allowsHitTesting(false)
                        .transition(.opacity)
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height)
            .animation(.easeOut(duration: 0.15), value: layers.toast)
        }
    }

    /// Меню — на 8 ниже кнопки, правым краем по правому краю кнопки; снизу не помещается — над кнопкой (app.js open).
    /// Нажатие мимо меню закрывает его.
    private func menuLayer(_ menu: PgMenu, at rect: CGRect, in size: CGSize) -> some View {
        let gap = CGFloat(PgSpace.s)
        let height = CGFloat(menu.items.count) * CGFloat(PgLayout.touchTarget) + 2 * CGFloat(PgSpace.xs)
        let below = rect.maxY + gap + height <= size.height - gap
        let trailing = max(gap, size.width - rect.maxX)
        let offset = below ? rect.maxY + gap : size.height - rect.minY + gap
        return ZStack {
            Color.clear
                .contentShape(Rectangle())
                .onTapGesture { layers.closeMenu() }
            PgMenuView(menu: menu) { index in
                layers.pick(index)
            }
            .fixedSize()
            .padding(.trailing, trailing)
            .padding(below ? .top : .bottom, max(0, offset))
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: below ? .topTrailing : .bottomTrailing)
        }
    }
}
