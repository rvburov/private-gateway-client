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

    func testLogLevelFollowsTheSettingAndTheConnectionLogStaysOff() throws {
        for level in CoreLogLevel.allCases {
            let log = try object(build(server(reality), CoreSettings(logLevel: level))["log"])
            XCTAssertEqual(log["loglevel"] as? String, level.xray)
            XCTAssertEqual(log["access"] as? String, "none", "\(level): адреса сайтов в журнал соединений не попадают")
            XCTAssertEqual(log["dnsLog"] as? Bool, false)
        }
    }

    func testSniffingAndMtuOfTheTunnelFollowTheSettings() throws {
        let inbound = try XCTUnwrap((build(server(reality), CoreSettings(sniffing: false, mtu: 1280))["inbounds"] as? [[String: Any]])?.first)
        XCTAssertEqual(try object(inbound["sniffing"])["enabled"] as? Bool, false)
        XCTAssertEqual(try object(inbound["settings"])["mtu"] as? Int, 1280)
        let on = try XCTUnwrap((build(server(reality), CoreSettings())["inbounds"] as? [[String: Any]])?.first)
        XCTAssertEqual(try object(on["sniffing"])["enabled"] as? Bool, true)
        XCTAssertEqual(try object(on["sniffing"])["routeOnly"] as? Bool, true, "имя — только для правил, соединение идет туда, куда просили")
    }

    func testTlsFragmentDialsTheServerThroughTheFragmentOutbound() throws {
        let trojan = try server("trojan://pw@198.51.100.7:443?sni=example.org#T")
        let plain = try build(trojan, CoreSettings())
        XCTAssertNil(try object(outbound(plain, "proxy")["streamSettings"])["sockopt"])
        XCTAssertFalse(try XCTUnwrap(plain["outbounds"] as? [[String: Any]]).contains { $0["tag"] as? String == XrayConfig.fragmentTag })

        for link in [reality, "trojan://pw@198.51.100.7:443?sni=example.org#T"] {
            let config = try build(server(link), CoreSettings(tlsFragment: true))
            let sockopt = try object(try object(outbound(config, "proxy")["streamSettings"])["sockopt"])
            XCTAssertEqual(sockopt["dialerProxy"] as? String, XrayConfig.fragmentTag, link)
            let fragment = try outbound(config, XrayConfig.fragmentTag)
            XCTAssertEqual(fragment["protocol"] as? String, "freedom")
            let settings = try object(fragment["settings"])
            let packets = try object(settings["fragment"])
            XCTAssertEqual(packets["packets"] as? String, "tlshello")
            XCTAssertEqual(packets["length"] as? String, "100-200")
            XCTAssertEqual(packets["interval"] as? String, "10-20")
            XCTAssertEqual((settings["finalRules"] as? [[String: Any]])?.first?["action"] as? String, "allow", "сервер в частной сети не закрывается")
            XCTAssertEqual(try XCTUnwrap(config["outbounds"] as? [[String: Any]]).first?["tag"] as? String, "proxy", "по умолчанию — по-прежнему сервер")
        }
    }

    func testSiteRulesGoAfterTheLocalNetworkAndIpv6MostPreciseFirst() throws {
        let rules = [
            SiteRule(pattern: "example.com", action: .direct), SiteRule(pattern: "*.local", action: .direct), SiteRule(pattern: "ads.example.org", action: .block),
            SiteRule(pattern: "198.51.100.0/24", action: .proxy), SiteRule(pattern: "2001:db8::/32", action: .block), SiteRule(pattern: "203.0.113.5", action: .direct),
        ]
        let split = try self.rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(mode: .split, rules: rules))))
        let plain = try self.rules(build(server(reality), CoreSettings()))
        XCTAssertEqual(try canonical(Array(split.prefix(plain.count))), try canonical(plain), "служебные правила — первыми и те же")
        let own = Array(split.dropFirst(plain.count))
        XCTAssertEqual(own.count, rules.count)
        // Точнее — раньше (решение 739): домены от длинного к короткому, адреса и подсети от узкой к широкой, равные — по списку.
        XCTAssertEqual(own.map { $0["domain"] as? [String] }, [["domain:ads.example.org"], ["domain:example.com"], ["domain:local"], nil, nil, nil])
        XCTAssertEqual(own.map { $0["ip"] as? [String] }, [nil, nil, nil, ["2001:db8::/32"], ["203.0.113.5"], ["198.51.100.0/24"]])
        XCTAssertEqual(own.map { $0["outboundTag"] as? String }, ["block", "direct", "direct", "block", "direct", "proxy"])
        XCTAssertTrue(own.allSatisfy { $0["type"] as? String == "field" && $0["inboundTag"] == nil })

        // «Весь трафик»: «Напрямую» ядро не получает, остальные — в том же порядке.
        let all = try self.rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(mode: .allTraffic, rules: rules))))
        XCTAssertEqual(Array(all.dropFirst(plain.count)).map { $0["outboundTag"] as? String }, ["block", "block", "proxy"])
        XCTAssertFalse(Array(all.dropFirst(plain.count)).contains { $0["outboundTag"] as? String == "direct" })
    }

    /// Решение 739: «Только этот адрес» — условием «full:», и точнее — раньше, как у десктопа и Android.
    func testExactAddressAndMorePreciseRulesGoFirst() throws {
        let rules = [
            SiteRule(pattern: "youtube.com", action: .block), SiteRule(pattern: "music.youtube.com", action: .proxy),
            SiteRule(pattern: "example.com", action: .block, exact: true), SiteRule(pattern: "*.example.com", action: .proxy),
        ]
        let config = try build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(rules: rules)))
        let own = try self.rules(config).compactMap { rule -> String? in
            (rule["domain"] as? [String]).map { $0[0] + " " + (rule["outboundTag"] as? String ?? "") }
        }
        XCTAssertEqual(own, ["full:example.com block", "domain:music.youtube.com proxy", "domain:youtube.com block", "domain:example.com proxy"])
    }

    /// Решение 740: «Остальные сайты: Блокировать» — разрешенные сайты, затем «Пропускается всегда» через сервер (и сайт
    /// Private Gateway), время по UDP-порту, остальное из туннеля — в блок. «Напрямую» в режиме «Весь трафик» — разрешение через
    /// VPN, в раздельном — напрямую.
    func testBlockOthersLetsThroughAllowedSitesAndSystemServicesAndBlocksTheRest() throws {
        let list = [
            SiteRule(pattern: "youtube.com", action: .proxy), SiteRule(pattern: "example.com", action: .direct),
            SiteRule(pattern: "ads.youtube.com", action: .block),
        ]
        let all = try rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(rules: list, blockOthers: true))))
        let tail = Array(all.suffix(6))
        XCTAssertEqual(
            tail.prefix(3).map { ($0["domain"] as? [String])?.first ?? "" },
            ["domain:ads.youtube.com", "domain:youtube.com", "domain:example.com"]
        )
        XCTAssertEqual(tail.prefix(3).map { $0["outboundTag"] as? String }, ["block", "proxy", "proxy"])
        XCTAssertEqual(tail[3]["domain"] as? [String], (AlwaysAllowed.domains + ["private-gateway.duckdns.org"]).map { "domain:" + $0 })
        XCTAssertEqual(tail[3]["outboundTag"] as? String, "proxy")
        XCTAssertEqual([tail[4]["network"], tail[4]["port"], tail[4]["outboundTag"]].map { $0 as? String }, ["udp", "123", "proxy"], "точное время (NTP)")
        XCTAssertEqual(tail[5]["inboundTag"] as? [String], [XrayConfig.tunTag])
        XCTAssertEqual([tail[5]["network"], tail[5]["outboundTag"]].map { $0 as? String }, ["tcp,udp", "block"])

        let split = try rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(mode: .split, rules: list, blockOthers: true))))
        XCTAssertEqual(split.first { ($0["domain"] as? [String]) == ["domain:example.com"] }?["outboundTag"] as? String, "direct")
        XCTAssertEqual(split.last?["outboundTag"] as? String, "block")
        let open = try rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), RoutingSettings(rules: list))))
        XCTAssertFalse(open.contains { $0["network"] != nil }, "по умолчанию остальное открывается")
    }

    /// Решение 741: «Все адреса сервиса» — домены сервиса из списка, базы geosite у ядра телефона нет.
    func testAllAddressesOfAServiceGoToTheCoreAsItsDomains() throws {
        let routing = RoutingSettings(rules: [SiteRule(pattern: "geosite:youtube", action: .proxy), SiteRule(pattern: "music.youtube.com", action: .block)])
        let own = try rules(build(server(reality), TunnelSettings().core(ConnectionSettings(), routing))).filter { $0["domain"] != nil }
        XCTAssertEqual(own[0]["domain"] as? [String], ["domain:music.youtube.com"], "точнее — раньше")
        XCTAssertEqual(own[1]["domain"] as? [String], ServiceSites.of(pattern: "geosite:youtube")?.conditions)
        XCTAssertEqual(own[1]["outboundTag"] as? String, "proxy")
    }

    /// «Пропускается всегда» одно на все платформы: shared/specifications/routing/always-allowed.json.
    func testAlwaysAllowedMatchesTheSharedList() throws {
        let file = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/routing/always-allowed.json")
        let shared = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
        let groups = try XCTUnwrap(shared["domains"] as? [String: [String]])
        XCTAssertEqual(groups["updates"], AlwaysAllowed.updates)
        XCTAssertEqual(groups["time"], AlwaysAllowed.time)
        XCTAssertEqual(groups["network"], AlwaysAllowed.network)
        XCTAssertEqual(groups["notifications"], AlwaysAllowed.notifications)
        XCTAssertEqual(groups["certificates"], AlwaysAllowed.certificates)
        XCTAssertEqual(Set(groups.keys), ["updates", "time", "network", "notifications", "certificates"])
        XCTAssertEqual(shared["udpPorts"] as? [Int], AlwaysAllowed.udpPorts)
    }

    func testGeositeAndBadPatternsNeverReachTheCore() throws {
        let rules = [
            SiteRule(pattern: "geosite:category-ads", action: .block), SiteRule(pattern: "not a site", action: .direct),
            SiteRule(pattern: " Example.ORG ", action: .block),
        ]
        let config = try build(server(reality), CoreSettings(siteRules: rules))
        let service = try self.rules(build(server(reality), CoreSettings())).count
        let own = Array(try self.rules(config).dropFirst(service))
        XCTAssertEqual(own.count, 1, "у ядра телефона нет базы geosite: с таким правилом оно бы не запустилось")
        XCTAssertEqual(own.first?["domain"] as? [String], ["domain:example.org"])
        let text = try XCTUnwrap(XrayConfig.build(try server(reality), CoreSettings(siteRules: rules), tunFd: 3).json)
        XCTAssertFalse(text.contains("geosite"))
    }

    func testSubscriptionInputStaysFirstBeforeTheSiteRules() throws {
        let proxy = SubscriptionProxy(port: 32123, user: "test-user", password: "test-password")
        let rules = [SiteRule(pattern: "example.com", action: .direct)]
        let text = try XCTUnwrap(XrayConfig.build(try server(reality), CoreSettings(siteRules: rules), tunFd: 3, subscriptionProxy: proxy).json)
        let config = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
        let first = try XCTUnwrap(try self.rules(config).first)
        XCTAssertEqual(first["inboundTag"] as? [String], [XrayConfig.subscriptionTag])
        XCTAssertEqual(first["outboundTag"] as? String, XrayConfig.proxyTag, "правило «Напрямую» не отправляет подписку мимо сервера")
    }

    // MARK: Помощники

    /// FakeDNS (решение 749), как у десктопа и Android: пул, настоящий DNS для имени сервера, распознавание по пулу, остаток пула — в блок.
    func testFakeDnsAnswersFromItsPoolAndKeepsTheRealAddressForTheServerName() throws {
        let plain = try build(try server(reality), CoreSettings())
        XCTAssertNil(plain["fakedns"], "FakeDNS по умолчанию выключен")
        XCTAssertEqual(try sniffing(plain)["destOverride"] as? [String], ["http", "tls", "quic"])

        let byName = try server(reality.replacingOccurrences(of: "203.0.113.24", with: "server.example.com"))
        let fake = try build(byName, CoreSettings(ipv6: true, fakeDns: true))
        let pool = try object((fake["fakedns"] as? [Any])?.first)
        XCTAssertEqual(pool["ipPool"] as? String, XrayConfig.fakeDnsPool)
        XCTAssertEqual(pool["poolSize"] as? Int, 65535)
        let servers = try XCTUnwrap(try object(fake["dns"])["servers"] as? [Any])
        let real = try object(servers.first)
        XCTAssertEqual(real["domains"] as? [String], ["full:server.example.com"], "имя сервера — настоящим адресом")
        XCTAssertEqual(real["skipFallback"] as? Bool, true)
        XCTAssertEqual(servers.dropFirst().compactMap { $0 as? String }, ["fakedns", XrayConfig.defaultDns])
        XCTAssertEqual(try sniffing(fake)["destOverride"] as? [String], ["http", "tls", "quic", "fakedns"])
        XCTAssertEqual(try sniffing(fake)["routeOnly"] as? Bool, true)
        XCTAssertEqual(try object(try outbound(fake, XrayConfig.directTag)["settings"])["domainStrategy"] as? String, "UseIPv4v6")
        XCTAssertEqual(try object(fake["routing"])["domainStrategy"] as? String, "AsIs")
        let poolRule = try rules(fake)[1]
        XCTAssertEqual(poolRule["ip"] as? [String], [XrayConfig.fakeDnsPool], "остаток пула — в блок сразу после DNS")
        XCTAssertEqual(poolRule["outboundTag"] as? String, XrayConfig.blockTag)

        // Сервер по адресу — настоящий DNS ему не нужен; без распознавания — имя только из адреса пула.
        let byAddress = try build(try server(reality), CoreSettings(allowLan: true, sniffing: false, fakeDns: true))
        XCTAssertEqual(try object(byAddress["dns"])["servers"] as? [String], ["fakedns", XrayConfig.defaultDns])
        XCTAssertEqual(try sniffing(byAddress)["destOverride"] as? [String], ["fakedns"])
        XCTAssertEqual(try sniffing(byAddress)["metadataOnly"] as? Bool, true)
        XCTAssertEqual(try object(byAddress["routing"])["domainStrategy"] as? String, "IPIfNonMatch", "локальной сети нужен адрес")
        XCTAssertEqual(try object(try outbound(byAddress, XrayConfig.directTag)["settings"])["domainStrategy"] as? String, "UseIPv4")
        XCTAssertTrue(TunnelSettings().needsReconnect(TunnelSettings(fakeDns: true)))
        XCTAssertFalse(TunnelSettings().needsReconnect(TunnelSettings(checkLeaks: false)), "проверка — дело окна, не ядра")
    }

    private func sniffing(_ config: [String: Any]) throws -> [String: Any] {
        let tun = try XCTUnwrap((config["inbounds"] as? [[String: Any]])?.first { $0["tag"] as? String == XrayConfig.tunTag })
        return try object(tun["sniffing"])
    }

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
