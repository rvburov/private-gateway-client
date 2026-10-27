import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Те же случаи, что у десктопа (`SecretMaskerTests`) и Android (`SecretMaskerTest`); значения заведомо ненастоящие.
final class SecretMaskerTests: XCTestCase {
    private let hidden = SecretMasker.hidden

    func testServerLinkIsHiddenAfterScheme() {
        let links = [
            "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=reality&pbk=Abc#NL",
            "VMESS://eyJhZGQiOiIyMDMuMC4xMTMuMjQifQ==",
            "trojan://password@203.0.113.24:443",
            "ss://YWVzLTI1Ni1nY206cGFzcw@203.0.113.24:8388",
            "hysteria2://pass@203.0.113.24:443?sni=example.com",
            "hy2://pass@203.0.113.24:443",
            "tuic://uuid:pass@203.0.113.24:443",
            "wireguard://key@203.0.113.24:51820",
        ]
        for link in links {
            let scheme = link.components(separatedBy: "://")[0]
            XCTAssertEqual(SecretMasker.mask("импорт \(link) готов"), "импорт \(scheme)://\(hidden) готов", link)
        }
    }

    func testSubscriptionUrlKeepsHostAndHidesPathAndQuery() {
        XCTAssertEqual(
            SecretMasker.mask("загрузка https://example.com/sub/AbCdEf123?token=xyz завершена"), "загрузка https://example.com/\(hidden) завершена"
        )
    }

    func testUrlWithoutPathIsLeftAsIs() {
        XCTAssertEqual(SecretMasker.mask("проверка https://example.com"), "проверка https://example.com")
    }

    func testUuidIsHidden() {
        XCTAssertEqual(SecretMasker.mask("id=11111111-2222-3333-4444-55555555abcd;"), "id=\(hidden);")
    }

    func testBase64KeyIsHidden() {
        // Похоже на ключ WireGuard: 44 знака base64 с «=» в конце.
        XCTAssertEqual(SecretMasker.mask("peer aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW3xY5zA7bC9d= ok"), "peer \(hidden) ok")
    }

    func testOrdinaryTextPathsAndHashesAreNotTouched() {
        let texts = [
            "/home/user/private-gateway-client/artifacts/bin/service",
            "служба запущена, ядро xray 25.10.15 готово",
            "0123456789abcdef0123456789abcdef0123456789abcdef",
            // Путь с цифрами и заглавными буквами похож на ключ, но начинается с «/».
            "журнал /Volumes/Backup2024/PrivateGatewayClient записан",
        ]
        for text in texts {
            XCTAssertEqual(SecretMasker.mask(text), text)
        }
    }

    func testNilAndEmptyGiveEmptyString() {
        XCTAssertEqual(SecretMasker.mask(nil), "")
        XCTAssertEqual(SecretMasker.mask(""), "")
    }
}
