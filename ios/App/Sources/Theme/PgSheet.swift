import PrivateGatewayTheme
import SwiftUI

extension View {
    /// Нижний лист (.sheet с полоской .grabber): системный лист iOS во всю высоту (`presentationDetents`, iOS 16) — свайп вниз
    /// и жест VoiceOver «назад» его закрывают — со своим видом: полоска-захват control.grabber 40 × 5, фон surface.solid,
    /// поля 10 · 16 · 20; скругление radius.panel и белый фон самого листа — с iOS 16.4. У листа свои слои (`PgLayers`):
    /// диалог, меню и подсказка из листа встают поверх него.
    func pgSheet<Sheet: View>(isPresented: Binding<Bool>, @ViewBuilder content: @escaping () -> Sheet) -> some View {
        sheet(isPresented: isPresented) {
            PgSheet(content: content)
        }
    }
}

/// Содержимое нижнего листа: полоска-захват сверху и блоки листа под ней.
struct PgSheet<Content: View>: View {
    @ViewBuilder let content: () -> Content

    @StateObject private var layers = PgLayers()

    var body: some View {
        VStack(spacing: 0) {
            Capsule()
                .fill(Color(PgColor.Control.grabber))
                .frame(width: 40, height: 5)
                .padding(.bottom, PgSpace.m)
                .accessibilityHidden(true)
            content()
        }
        .padding(.top, PgSpace.m)
        .padding(.horizontal, PgSpace.windowSideMobile)
        .padding(.bottom, PgSpace.windowSide)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(Color(PgColor.Surface.solid).ignoresSafeArea())
        .pgLayers(layers)
        .presentationDetents([.large])
        .presentationDragIndicator(.hidden)
        .modifier(PgSheetLook())
    }
}

/// Шапка нижнего листа (.sheet доски: «Свой MTU», правило для сайта): заголовок type.status в одну строку и крестик
/// «Закрыть» — значок 20 линией 2,2 цветом text.secondary в поле касания layout.touchTarget.
struct PgSheetHeader: View {
    let title: String
    /// Подпись крестика для VoiceOver.
    let closeLabel: String
    var identifier = ""
    let onClose: () -> Void

    var body: some View {
        HStack(spacing: PgSpace.s) {
            Text(title)
                .pgText(PgType.status)
                .foregroundStyle(Color(PgColor.Text.primary))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .accessibilityAddTraits(.isHeader)
            Button(action: onClose) {
                PgIcon(data: PgIcons.close, color: Color(PgColor.Text.secondary), size: 20, lineWidth: 2.2)
                    .frame(width: PgLayout.touchTarget, height: PgLayout.touchTarget)
                    .contentShape(Rectangle())
            }
            .buttonStyle(PgPressStyle())
            .accessibilityLabel(closeLabel)
            .accessibilityIdentifier(identifier)
        }
    }
}

/// Скругление radius.panel и белый фон системного листа: задать их можно только с iOS 16.4, раньше они системные.
private struct PgSheetLook: ViewModifier {
    @ViewBuilder
    func body(content: Content) -> some View {
        if #available(iOS 16.4, *) {
            content
                .presentationCornerRadius(CGFloat(PgRadius.panel))
                .presentationBackground(Color(PgColor.Surface.solid))
        } else {
            content
        }
    }
}
