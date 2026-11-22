import PrivateGatewayTheme
import SwiftUI
import XCTest
@testable import PrivateGateway

/// Общие блоки телефона: длинное предупреждение читается целиком, маленькое действие доступно для касания.
final class StatusControlsTests: XCTestCase {
    @MainActor
    func testSmallActionsKeepTouchTargetInBothLanguagesAndWhileBusy() throws {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            for busy in [false, true] {
                let image = try XCTUnwrap(ImageRenderer(content: PgSmallButton(
                    label: texts.subsUpdate, icon: PgIcons.refresh, soft: true, spinning: busy, action: {}
                )).uiImage)
                XCTAssertGreaterThanOrEqual(image.size.height, PgLayout.touchTarget)
            }
        }
    }

    @MainActor
    func testWarningGrowsForWrappedTextInsteadOfClipping() throws {
        for language in PgTexts.Language.allCases {
            let texts = PgTexts(language)
            let short = try XCTUnwrap(ImageRenderer(content: PgBanner(icon: PgIcons.warning) {
                Text(texts.routingWarning).pgText(PgType.small).fixedSize(horizontal: false, vertical: true)
            }.frame(width: 393)).uiImage)
            let narrow = try XCTUnwrap(ImageRenderer(content: PgBanner(icon: PgIcons.warning) {
                Text(texts.routingWarning).pgText(PgType.small).fixedSize(horizontal: false, vertical: true)
            }.frame(width: 220)).uiImage)
            XCTAssertGreaterThan(narrow.size.height, short.size.height)
        }
    }
}
