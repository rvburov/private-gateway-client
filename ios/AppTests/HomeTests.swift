import PrivateGatewayCore
import PrivateGatewayTheme
import SwiftUI
import UIKit
import XCTest
@testable import PrivateGateway

/// Тема SwiftUI и главная на симуляторе: цвет токена не теряет каналов, насыщенность шрифта — ближайшая, иконка ложится в
/// свой прямоугольник, чипы и вид состояния — по состоянию туннеля, главная рисуется в каждом состоянии.
final class HomeTests: XCTestCase {
    private let texts = PgTexts(.russian)

    @MainActor
    func testTokenColorKeepsItsChannels() {
        var red: CGFloat = 0
        var green: CGFloat = 0
        var blue: CGFloat = 0
        var alpha: CGFloat = 0
        XCTAssertTrue(UIColor(Color(PgRGBA(argb: 0x8020_40FF))).getRed(&red, green: &green, blue: &blue, alpha: &alpha))
        XCTAssertEqual(alpha, 128.0 / 255, accuracy: 0.002)
        XCTAssertEqual(red, 32.0 / 255, accuracy: 0.002)
        XCTAssertEqual(green, 64.0 / 255, accuracy: 0.002)
        XCTAssertEqual(blue, 1, accuracy: 0.002)
    }

    @MainActor
    func testFontWeightIsTheNearestOne() {
        XCTAssertEqual(PgType.body.fontWeight, .regular)
        XCTAssertEqual(PgType.nav.fontWeight, .medium)
        XCTAssertEqual(PgType.title.fontWeight, .semibold)
        XCTAssertEqual(PgType.display.fontWeight, .bold)
        XCTAssertEqual(PgType.body.extraLineSpacing, (1.45 - 1.2) * 14, accuracy: 0.001)
        XCTAssertEqual(PgType.title.extraLineSpacing, 0)
    }

    @MainActor
    func testIconFitsItsRectangle() {
        let rect = CGRect(x: 10, y: 20, width: 48, height: 48)
        for (name, data) in PgIcons.all {
            let bounds = PgIconShape(data: data).path(in: rect).boundingRect
            XCTAssertGreaterThan(bounds.width + bounds.height, 0, name)
            XCTAssertTrue(rect.insetBy(dx: -1, dy: -1).contains(bounds), "\(name): \(bounds)")
        }

        // Щит: x 4,5–19,5 и y 2,8–21,2 из 24 → вдвое крупнее и со сдвигом прямоугольника.
        let shield = PgIconShape(data: PgIcons.shield).path(in: rect).boundingRect
        XCTAssertEqual(shield.minX, 10 + 4.5 * 2, accuracy: 0.01)
        XCTAssertEqual(shield.minY, 20 + 2.8 * 2, accuracy: 0.01)
        XCTAssertEqual(shield.maxX, 10 + 19.5 * 2, accuracy: 0.01)
    }

    @MainActor
    func testChipsFollowTheTunnel() {
        let connected = ProtectionChips.of(.connected, killSwitch: true, texts)
        XCTAssertEqual(connected.map(\.value), [texts.checkAllOk, texts.checkDnsOk, texts.checkIpv6Ok, texts.checkKsOk])
        XCTAssertEqual(connected.map(\.look), [.ok, .ok, .ok, .ok])

        let connecting = ProtectionChips.of(.reconnecting, killSwitch: true, texts)
        XCTAssertEqual(connecting.map(\.look), [.waiting, .waiting, .waiting, .ok], "блок уже действует, пока туннель поднимается")
        XCTAssertEqual(connecting[3].value, texts.checkKsWaiting)

        let off = ProtectionChips.of(.off, killSwitch: true, texts)
        XCTAssertEqual(off.map(\.value), [texts.checkAllOff, texts.checkDnsOff, texts.checkIpv6Off, texts.checkKsOff])
        XCTAssertEqual(off.map(\.look), [.off, .off, .off, .off], "без туннеля блока нет")

        XCTAssertEqual(ProtectionChips.of(.connected, killSwitch: false, texts)[3].look, .off)
        XCTAssertEqual(ProtectionChips.of(.error, killSwitch: true, texts).map(\.title), [texts.checkAllTitle, texts.checkDnsTitle, texts.checkIpv6Title, texts.checkKsTitle])
    }

    @MainActor
    func testStateLookIsColorAndText() {
        XCTAssertEqual(StateLook.of(.connected, texts).title, "IP скрыт")
        XCTAssertEqual(StateLook.of(.connected, texts).ring, PgState.Connected.ring)
        XCTAssertEqual(StateLook.of(.off, texts).power, texts.powerLabelOff)
        XCTAssertEqual(StateLook.of(.reconnecting, texts).ring, StateLook.of(.connecting, texts).ring, "переподключение — вид подключения")
        XCTAssertNotEqual(StateLook.of(.error, texts).mark, StateLook.of(.connected, texts).mark, "состояние видно не только цветом")
        XCTAssertEqual(StatusCard.online(Date(timeIntervalSince1970: 0), now: Date(timeIntervalSince1970: 8_049)), "02:14:09")
    }

    @MainActor
    func testHomeDrawsInEveryStateAndLanguage() throws {
        for language in PgTexts.Language.allCases {
            for state in TunnelState.allCases {
                let status = TunnelStatus(state: state, since: Date(), error: state == .error ? TunnelErrors.coreFailed : nil, attempt: 2)
                let view = HomeView(texts: PgTexts(language), status: status, version: "0.1.0").frame(width: 393, height: 852)
                let image = try XCTUnwrap(ImageRenderer(content: view).uiImage, "\(language) \(state)")
                XCTAssertEqual(image.size.width, 393, accuracy: 1)
            }
        }
    }
}
