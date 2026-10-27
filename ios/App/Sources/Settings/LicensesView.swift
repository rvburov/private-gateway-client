import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI

/// «Лицензии открытых компонентов» — как `LicensesScreen` Android (решение 449): компоненты приложения из shared/licenses/ios.json
/// (тест сверяет список со сборкой) — имя и лицензия, по нажатию — авторство, ссылки, версия и полный текст лицензии.
struct LicensesView: View {
    let texts: PgTexts
    let about: AboutInfo
    let onBack: () -> Void

    @State private var open: String?

    init(texts: PgTexts, about: AboutInfo, open: String? = nil, onBack: @escaping () -> Void) {
        self.texts = texts
        self.about = about
        self.onBack = onBack
        _open = State(initialValue: open)
    }

    var body: some View {
        if let shown = about.components.first(where: { $0.name == open }) {
            LicenseDetail(texts: texts, component: shown, version: about.version(shown), licenseText: about.licenseText) { open = nil }
        } else {
            list
        }
    }

    private var list: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                PgPageHeader(title: texts.aboutLicenses, texts: texts, onBack: onBack)
                Text(texts.aboutLicensesText).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                VStack(spacing: 0) {
                    ForEach(about.components, id: \.name) { component in
                        Button {
                            open = component.name
                        } label: {
                            HStack(spacing: PgSpace.m) {
                                Text(component.name).pgText(PgType.bodyStrong).foregroundStyle(Color(PgColor.Text.primary))
                                Spacer(minLength: PgSpace.s)
                                Text(component.label).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                                PgIcon(data: PgIcons.chevronRight, color: Color(PgColor.Text.muted), size: 20)
                            }
                            .frame(minHeight: PgLayout.touchTarget)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("licenses.\(component.name)")
                    }
                }
                .pgCard()
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("licenses")
    }
}

/// Один компонент: подпись лицензии, версия, авторство, ссылки (открываются в Safari) и текст лицензии.
struct LicenseDetail: View {
    let texts: PgTexts
    let component: OpenComponent
    let version: String?
    let licenseText: (String) -> String
    let onBack: () -> Void

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: PgSpace.l) {
                PgPageHeader(title: component.name, texts: texts, onBack: onBack)
                VStack(alignment: .leading, spacing: PgSpace.xs) {
                    Text(component.label).pgText(PgType.title).foregroundStyle(Color(PgColor.Text.primary)).accessibilityIdentifier("license.label")
                    if let version {
                        Text(texts.androidLicensesVersion(version)).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    }

                    if let inside = component.inside {
                        Text(texts.androidLicensesInside(inside)).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    }

                    Text(component.copyright)
                        .pgText(PgType.body)
                        .foregroundStyle(Color(PgColor.Text.primary))
                        .padding(.top, PgSpace.s)
                        .accessibilityIdentifier("license.copyright")
                    if let notice = component.notice {
                        Text(notice).pgText(PgType.small).foregroundStyle(Color(PgColor.Text.secondary))
                    }

                    LinkRow(url: component.url, identifier: "license.url").padding(.top, PgSpace.s)
                    if let source = component.sourceUrl(version: version) {
                        Text(texts.androidLicensesSource).pgText(PgType.caption).foregroundStyle(Color(PgColor.Text.secondary))
                        LinkRow(url: source, identifier: "license.source")
                    }
                }
                .pgCard()

                ForEach(component.licenseIds, id: \.self) { id in
                    VStack(alignment: .leading, spacing: PgSpace.s) {
                        Overline(text: texts.androidLicensesText + " · " + id)
                        Text(licenseText(id))
                            .font(.system(size: PgType.caption.size, design: .monospaced))
                            .foregroundStyle(Color(PgColor.Text.primary))
                            .textSelection(.enabled)
                            .accessibilityIdentifier("license.text.\(id)")
                    }
                    .pgCard()
                }
            }
            .padding(.horizontal, PgSpace.windowSideMobile)
            .padding(.bottom, PgLayout.scrollBottomPadding)
        }
        .accessibilityIdentifier("license")
    }
}

/// Ссылка компонента: адрес целиком, акцентным цветом; неверный адрес — просто текст.
private struct LinkRow: View {
    let url: String
    let identifier: String

    var body: some View {
        let label = Text(url).pgText(PgType.small).foregroundStyle(Color(PgColor.Accent.text)).frame(maxWidth: .infinity, minHeight: PgLayout.touchTarget, alignment: .leading)
        if let destination = URL(string: url), !url.isEmpty {
            Link(destination: destination) { label }.accessibilityIdentifier(identifier)
        } else {
            label
        }
    }
}
