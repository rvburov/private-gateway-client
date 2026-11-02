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
