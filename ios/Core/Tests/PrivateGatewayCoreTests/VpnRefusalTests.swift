import Foundation
import PrivateGatewayCore
import XCTest

/// «Запретить» в системном запросе VPN (DECISIONS.md, п. 5) — не «Работает другой VPN».
final class VpnRefusalTests: XCTestCase {
    func testDeniedPermissionIsRecognized() {
        XCTAssertTrue(VpnRefusal.permissionDenied(NSError(domain: "NEVPNErrorDomain", code: 5, userInfo: [NSLocalizedDescriptionKey: "permission denied"])))
    }

    func testOtherRefusalsStayOtherVpn() {
        XCTAssertFalse(VpnRefusal.permissionDenied(NSError(domain: "NEVPNErrorDomain", code: 1)), "профиль неверный")
        XCTAssertFalse(VpnRefusal.permissionDenied(NSError(domain: "NEVPNErrorDomain", code: 3)), "подключение не удалось")
        XCTAssertFalse(VpnRefusal.permissionDenied(NSError(domain: NSCocoaErrorDomain, code: 5)), "чужой домен")
        XCTAssertFalse(VpnRefusal.permissionDenied(TunnelFailure(TunnelErrors.otherVpn)))
    }
}
