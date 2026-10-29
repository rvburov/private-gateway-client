import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Конфиг Xray на телефоне — те же случаи, что `XrayConfigTest` Android: выход сервера — те же поля, что у службы десктопа;
/// вход — TUN с дескриптором; DNS из туннеля — через сервер; локальная сеть и IPv6 — по настройкам; журнал соединений
/// выключен.
final class XrayConfigTests: XCTestCase {
    private let reality = "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?type=tcp&security=reality&sni=www.example.com&fp=chrome"
        + "&pbk=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb&sid=0123abcd&flow=xtls-rprx-vision#NL"

    func testVlessRealityGoesOutThroughTheServerAndTheTunnelComesInWithItsFd() throws {
        let config = try build(server(reality), CoreSettings(), fd: 42)

        XCTAssertEqual(try object(config["env"])["xray.tun.fd"] as? String, "42")
        let inbounds = try XCTUnwrap(config["inbounds"] as? [[String: Any]])
        XCTAssertEqual(inbounds.count, 1)
        XCTAssertEqual(inbounds[0]["protocol"] as? String, "tun")
        XCTAssertEqual(try object(inbounds[0]["settings"])["mtu"] as? Int, 1500)
        XCTAssertEqual(try object(config["log"])["access"] as? String, "none", "адреса сайтов в журнал не попадают")

        let proxy = try outbound(config, "proxy")
        let vnext = try XCTUnwrap(try object(proxy["settings"])["vnext"] as? [[String: Any]])
        let user = try XCTUnwrap((vnext[0]["users"] as? [[String: Any]])?.first)
        XCTAssertEqual(user["flow"] as? String, "xtls-rprx-vision")
        let stream = try object(proxy["streamSettings"])
        XCTAssertEqual(stream["network"] as? String, "raw")
        let realitySettings = try object(stream["realitySettings"])
        XCTAssertEqual(realitySettings["serverName"] as? String, "www.example.com")
        XCTAssertEqual(realitySettings["fingerprint"] as? String, "chrome")
        let tags = try XCTUnwrap(config["outbounds"] as? [[String: Any]]).map { $0["tag"] as? String }
        XCTAssertEqual(tags, ["proxy", "direct", "block", "dns-out"])
        // Выход DNS: A и AAAA отвечает DNS конфига, остальное — REFUSED; «nonIPQuery» Xray-core 26.x объявил к удалению.
        let dns = try object(outbound(config, "dns-out")["settings"])
        XCTAssertNil(dns["nonIPQuery"])
        let dnsRules = try XCTUnwrap(dns["rules"] as? [[String: Any]])
        XCTAssertEqual(dnsRules.count, 2)
        XCTAssertEqual(dnsRules[0]["action"] as? String, "hijack")
        XCTAssertEqual(dnsRules[0]["qType"] as? String, "1,28")
        XCTAssertEqual(dnsRules[1]["action"] as? String, "return")
        XCTAssertEqual(dnsRules[1]["rCode"] as? Int, 5)
    }

    func testDnsFromTheTunnelAndLocalNetworkAndIpv6FollowTheSettings() throws {
        let closed = try rules(build(server(reality), CoreSettings()))
        XCTAssertEqual(closed[0]["outboundTag"] as? String, "dns-out")
        XCTAssertEqual(closed[0]["port"] as? String, "53")
        XCTAssertEqual(closed[1]["outboundTag"] as? String, "block", "локальная сеть по умолчанию закрыта")
        XCTAssertEqual(closed[2]["ip"] as? [String], ["::/0"], "IPv6 без настройки — в блок")

        let open = try build(server(reality), CoreSettings(allowLan: true, ipv6: true))
        XCTAssertEqual(try rules(open)[1]["outboundTag"] as? String, "direct")
        XCTAssertEqual(try rules(open).count, 2)
        XCTAssertEqual(try object(open["dns"])["queryStrategy"] as? String, "UseIP")
        XCTAssertEqual(try object(build(server(reality), CoreSettings())["dns"])["queryStrategy"] as? String, "UseIPv4")
    }

    func testSubscriptionProxyIsLocalAuthenticatedAndForcedThroughTheServerBeforeLanRules() throws {
        let proxy = SubscriptionProxy(port: 32123, user: "test-user", password: "test-password")
        let settings = CoreSettings(allowLan: true, lanDns: "192.168.1.53")
        let text = try XCTUnwrap(XrayConfig.build(try server(reality), settings, tunFd: 42, subscriptionProxy: proxy).json)
        let config = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
        let inbounds = try XCTUnwrap(config["inbounds"] as? [[String: Any]])
        let input = try XCTUnwrap(inbounds.first { $0["tag"] as? String == XrayConfig.subscriptionTag })
        XCTAssertEqual(input["protocol"] as? String, "http")
        XCTAssertEqual(input["listen"] as? String, "127.0.0.1")
        XCTAssertEqual(input["port"] as? Int, 32123)
        let accounts = try XCTUnwrap(try object(input["settings"])["accounts"] as? [[String: String]])
        XCTAssertEqual(accounts, [["user": "test-user", "pass": "test-password"]])
        let first = try XCTUnwrap(try rules(config).first)
        XCTAssertEqual(first["inboundTag"] as? [String], [XrayConfig.subscriptionTag])
        XCTAssertEqual(first["outboundTag"] as? String, XrayConfig.proxyTag, "локальная сеть и DNS не отправляют подписку напрямую")
    }

    func testOwnDnsInAPrivateNetworkIsAskedDirectlyEvenWithTheLanClosed() throws {
        for (custom, direct) in [("192.168.1.53", true), ("fd00::53", true), ("9.9.9.9", false), ("https://192.168.1.53/dns-query", false)] {
            let settings = TunnelSettings(dns: .custom, customDns: custom)
            let config = try build(server(reality), settings.core())
            let list = try rules(config)
            let localBlock = try XCTUnwrap(list.firstIndex { $0["ip"] as? [String] == XrayConfig.localNetworks }, custom)
            XCTAssertEqual(list[localBlock]["outboundTag"] as? String, "block", custom)

            let own = list.firstIndex { $0["ip"] as? [String] == [custom] }
            if direct {
                XCTAssertEqual(own, localBlock - 1, "\(custom): раньше закрытия локальной сети")
                let rule = list[localBlock - 1]
                XCTAssertEqual(rule["outboundTag"] as? String, "direct", custom)
                XCTAssertEqual(rule["port"] as? String, "53", custom)
                XCTAssertNil(rule["inboundTag"], "\(custom): это запросы самого Xray, не программ из туннеля")
                XCTAssertEqual(list.firstIndex { $0["outboundTag"] as? String == "dns-out" }, 0, "\(custom): DNS программ — по-прежнему выходом DNS")
                XCTAssertEqual(try object(config["dns"])["servers"] as? [String], [custom])
            } else {
                XCTAssertNil(own, custom)
                XCTAssertFalse(list.contains { $0["outboundTag"] as? String == "direct" }, custom)
            }
        }
    }

    func testEmptyLanDnsAddsNoRule() throws {
        let plain = try rules(build(server(reality), CoreSettings()))
        XCTAssertEqual(try canonical(plain), try canonical(rules(build(server(reality), CoreSettings(lanDns: "")))))
        XCTAssertEqual(try rules(build(server(reality), CoreSettings(lanDns: "192.168.1.53"))).count, plain.count + 1, "правило добавляется одно")
    }

    func testEachProtocolBuildsTheSameOutboundAsTheDesktop() throws {
        let vmessJson = #"{"v":"2","ps":"WS","add":"cdn.example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","net":"ws","path":"/ws","host":"cdn.example.com","tls":"tls","sni":"cdn.example.com","alpn":"h2,http/1.1"}"#
        let vmess = try outbound(build(server("vmess://" + Data(vmessJson.utf8).base64EncodedString()), CoreSettings(mux: true)), "proxy")
        XCTAssertEqual(vmess["protocol"] as? String, "vmess")
        let ws = try object(vmess["streamSettings"])
        XCTAssertEqual(try object(ws["wsSettings"])["path"] as? String, "/ws")
        XCTAssertEqual(try object(ws["tlsSettings"])["alpn"] as? [String], ["h2", "http/1.1"])
        XCTAssertEqual(try object(vmess["mux"])["enabled"] as? Bool, true, "mux без flow включается")
        XCTAssertNil(try outbound(build(server(reality), CoreSettings(mux: true)), "proxy")["mux"], "mux несовместим с Vision")

        let trojan = try outbound(build(server("trojan://pw@198.51.100.7:8443?type=grpc&serviceName=tunnel&sni=example.org#T"), CoreSettings()), "proxy")
        XCTAssertEqual(try object(try object(trojan["streamSettings"])["grpcSettings"])["serviceName"] as? String, "tunnel")
        XCTAssertEqual((try object(trojan["settings"])["servers"] as? [[String: Any]])?.first?["password"] as? String, "pw")

        let ss = try outbound(build(server("ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@[2001:db8::5]:8388#SS"), CoreSettings()), "proxy")
        XCTAssertEqual((try object(ss["settings"])["servers"] as? [[String: Any]])?.first?["method"] as? String, "2022-blake3-aes-128-gcm")

        let wg = try outbound(build(server("wireguard://aaaa%3D@192.0.2.33:51820?publickey=cccc%3D&address=10.0.0.2/32&mtu=1420#WG"), CoreSettings()), "proxy")
        let wgSettings = try object(wg["settings"])
        XCTAssertEqual((wgSettings["peers"] as? [[String: Any]])?.first?["endpoint"] as? String, "192.0.2.33:51820")
        XCTAssertEqual(wgSettings["mtu"] as? Int, 1420)
        XCTAssertEqual(wgSettings["address"] as? [String], ["10.0.0.2/32"])
        XCTAssertEqual(wgSettings["noKernelTun"] as? Bool, true, "свой стек Xray, а не адаптер wgN — как у десктопа и Android")
    }

    func testServerWithoutEncryptionOnAnOpenAddressIsRefusedAsLibXrayDoes() throws {
        let cases: [(protocol: String, host: String, security: String, refused: Bool)] = [
            ("vless", "2001:db8::1", "none", true), ("vless", "[2001:db8::1]", "none", true), ("vless", "vpn.example.com", "none", true),
            ("vless", "vpn.example.com.", "none", true), ("trojan", "vpn.example.com", "none", true), ("vless", "vpn.example.com", "tls", false),
            ("vless", "vpn.example.com", "reality", false), ("vmess", "vpn.example.com", "none", false), ("vless", "10.0.0.5", "none", false),
            ("vless", "192.0.2.1", "none", false), ("vless", "::ffff:192.168.1.5", "none", false), ("vless", "fd00::5", "none", false), ("vless", "[fd00::5]", "none", false),
            ("vless", "nas.lan", "none", false), ("vless", "nas.lan.", "none", false), ("vless", "router.home.arpa", "none", false), ("vless", "printer.local", "none", false),
            ("vless", "NAS", "none", false), ("vless", "lan.example.com", "none", true), ("vless", "mylan", "none", false),
            ("vless", "010.0.0.1", "none", true), ("vless", "0177.0.0.1", "none", true), ("vless", "1:2:3", "none", true),
        ]
        let vmess = "vmess://" + Data(
            #"{"v":"2","ps":"V","add":"cdn.example.com","port":"443","id":"11111111-2222-3333-4444-555555555555","aid":"0","net":"tcp"}"#.utf8
        ).base64EncodedString()
        for item in cases {
            var tested: ServerDefinition
            switch item.protocol {
            case "trojan": tested = try server("trojan://pw@198.51.100.7:8443?sni=example.org#T")
            case "vmess": tested = try server(vmess)
            default: tested = try server("vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=none#V")
            }

            tested.host = item.host
            switch item.security {
            case "tls": tested.security = SecurityOptions(.tls, sni: "vpn.example.com")
            case "reality": tested.security = try server(reality).security
            default: tested.security = .none
            }

            let what = "\(item.protocol) \(item.host) \(item.security)"
            XCTAssertEqual(XrayConfig.unencrypted(tested), item.refused, what)
            let expected: CoreRejection? = item.refused ? .unencrypted : nil
            XCTAssertEqual(XrayConfig.build(tested, CoreSettings(), tunFd: 1).rejection, expected, what)
            XCTAssertEqual(PingBatch.outbounds(tested, CoreSettings()).rejection, expected, "замер задержки: " + what)
        }
    }

    func testHysteria2AndTuicAndMissingTransportsAreRefused() throws {
        XCTAssertEqual(XrayConfig.build(try server("hy2://pw@192.0.2.1:443#H"), CoreSettings(), tunFd: 1).rejection, .unsupportedProtocol)
        XCTAssertEqual(
            XrayConfig.build(try server("tuic://11111111-2222-3333-4444-555555555555:pw@192.0.2.1:443#T"), CoreSettings(), tunFd: 1).rejection,
            .unsupportedProtocol
        )

        var noUuid = try server(reality)
        noUuid.credentials.uuid = nil
        XCTAssertEqual(XrayConfig.build(noUuid, CoreSettings(), tunFd: 1).rejection, .missingCredentials)
        XCTAssertNil(XrayConfig.build(noUuid, CoreSettings(), tunFd: 1).json)

        var quic = try server(reality)
        quic.transport = .quic
        XCTAssertEqual(XrayConfig.build(quic, CoreSettings(), tunFd: 1).rejection, .unsupportedTransport)
    }

    func testPinnedCertificateGoesToTheCoreAndAllowInsecureDoesNot() throws {
        let pinned = try server(
            "vless://11111111-2222-3333-4444-555555555555@198.51.100.210:443?security=tls&sni=example.com"
                + "&pcs=A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90%2C0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0&vcn=example.com%2Cexample.org#P"
        )
        let tls = try object(try object(outbound(build(pinned, CoreSettings()), "proxy")["streamSettings"])["tlsSettings"])
        XCTAssertEqual(tls["pinnedPeerCertSha256"] as? String, "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90,0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0")
        XCTAssertEqual(tls["verifyPeerCertByName"] as? String, "example.com,example.org")

        let insecure = try server("trojan://pw@198.51.100.7:443?sni=example.org&allowInsecure=1#T")
        let plain = try object(try object(outbound(build(insecure, CoreSettings()), "proxy")["streamSettings"])["tlsSettings"])
        XCTAssertNil(plain["allowInsecure"], "Xray-core 26.x конфиг с allowInsecure не запускает (решение 477)")
        XCTAssertNil(plain["pinnedPeerCertSha256"])
        XCTAssertNil(plain["verifyPeerCertByName"])
    }

    func testFingerprintSettingOverridesTheProfileAndRequestsAndResponsesSpeakApi3() throws {
        let tls = try server("trojan://pw@198.51.100.7:443?sni=example.org&fp=firefox#T")
        let profile = try object(try object(outbound(build(tls, CoreSettings()), "proxy")["streamSettings"])["tlsSettings"])
        XCTAssertEqual(profile["fingerprint"] as? String, "firefox")
        XCTAssertEqual(profile["serverName"] as? String, "example.org")
        let chosen = try object(try object(outbound(build(tls, CoreSettings(fingerprint: "safari")), "proxy")["streamSettings"])["tlsSettings"])
        XCTAssertEqual(chosen["fingerprint"] as? String, "safari")

        let request = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(XrayConfig.runRequest(#"{"a":1}"#).utf8)) as? [String: Any])
        XCTAssertEqual(request["apiVersion"] as? Int, 3)
        XCTAssertEqual(request["method"] as? String, "runXray")
        XCTAssertEqual(try object(request["payload"])["xrayJson"] as? String, #"{"a":1}"#)
        XCTAssertEqual(XrayResponse.parse(#"{"success":true,"data":{},"error":""}"#), XrayResponse(success: true, error: nil))
        XCTAssertEqual(XrayResponse.parse(#"{"success":false,"data":null,"error":"boom"}"#), XrayResponse(success: false, error: "boom"))
        XCTAssertFalse(XrayResponse.parse("not json").success)
        XCTAssertFalse(XrayResponse.parse(nil).success)
        XCTAssertEqual(XrayResponse.version(#"{"success":true,"data":{"version":"26.3.27"}}"#), "26.3.27")
        XCTAssertNil(XrayResponse.version(#"{"success":false,"data":{"version":"26.3.27"}}"#))
        XCTAssertNil(XrayResponse.version(#"{"success":true,"data":{"version":""}}"#))
        XCTAssertNil(XrayResponse.version(#"{"success":true,"data":null}"#))
        XCTAssertNil(XrayResponse.version(nil))
    }

    // MARK: Помощники

    private func server(_ link: String) throws -> ServerDefinition {
        let parsed = LinkParser.parse(link)
        return try XCTUnwrap(parsed.server, "ссылка не разобралась: \(parsed.error ?? "")")
    }

    private func build(_ server: ServerDefinition, _ settings: CoreSettings, fd: Int32 = 3) throws -> [String: Any] {
        let result = XrayConfig.build(server, settings, tunFd: fd)
        let json = try XCTUnwrap(result.json, "отказ: \(String(describing: result.rejection))")
        return try XCTUnwrap(JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any])
    }

    private func object(_ value: Any?) throws -> [String: Any] {
        try XCTUnwrap(value as? [String: Any])
    }

    private func outbound(_ config: [String: Any], _ tag: String) throws -> [String: Any] {
        let outbounds = try XCTUnwrap(config["outbounds"] as? [[String: Any]])
        return try XCTUnwrap(outbounds.first { $0["tag"] as? String == tag }, tag)
    }

    private func rules(_ config: [String: Any]) throws -> [[String: Any]] {
        try XCTUnwrap(try object(config["routing"])["rules"] as? [[String: Any]])
    }

    private func canonical(_ value: Any) throws -> String {
        String(decoding: try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys]), as: UTF8.self)
    }
}
