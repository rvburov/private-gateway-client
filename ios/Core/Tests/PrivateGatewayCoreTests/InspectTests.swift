import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Узел замера, внешний IP и его провайдер, сети iPhone и итог замера — те же случаи, что `PublicAddressTest`,
/// `PhoneNetworkTest` и итоги `ProbeRunnerTest` Android (и `ProbeTargetTests`, `AddressLookupTests` десктопа).
final class InspectTests: XCTestCase {
    func testHostIsNormalizedLikeTheDesktop() {
        let cases = [
            ("example.com", "example.com"), ("  Example.COM.  ", "example.com"), ("www.example.org", "www.example.org"), ("localhost", "localhost"),
            ("xn--e1afmkfd.xn--p1ai", "xn--e1afmkfd.xn--p1ai"), ("пример.рф", "xn--e1afmkfd.xn--p1ai"), ("203.0.113.10", "203.0.113.10"),
            ("2001:db8::10", "2001:db8::10"), ("[2001:DB8::10]", "2001:db8::10"), ("2001:0db8:0000::0010", "2001:db8::10"),
        ]
        for (text, expected) in cases {
            XCTAssertEqual(ProbeTarget.normalize(text), expected, text)
        }
    }

    func testAnythingElseIsNotAHost() {
        let cases: [String?] = [
            nil, "", "   ", "https://example.com/", "example.com/path", "example.com:443", "example.com; reboot", "$(id).example.com", "exa mple.com",
            "-example.com", "example-.com", "example..com", "1.2.3", "12345", "user@example.com", "--help", "exa_mple.com", "[example.com]", "пример..рф",
            "256.1.1.1",
        ]
        for text in cases {
            XCTAssertNil(ProbeTarget.normalize(text), text ?? "nil")
        }
    }

    func testNameLongerThanDnsAllowsIsNotAHostAndPortIs1To65535() {
        let long = String(repeating: "a", count: 63) + ".example.com"
        XCTAssertEqual(ProbeTarget.normalize(long), long)
        XCTAssertNil(ProbeTarget.normalize(String(repeating: "a", count: 64) + ".example.com"), "метка — не длиннее 63")
        XCTAssertNil(ProbeTarget.normalize(Array(repeating: String(repeating: "a", count: 62), count: 4).joined(separator: ".") + ".com"), "имя — не длиннее 253")
        XCTAssertEqual(ProbeTarget.port(" 443 "), 443)
        for text in ["0", "65536", "-1", "443a", "", nil] as [String?] {
            XCTAssertNil(ProbeTarget.port(text), text ?? "nil")
        }
    }

    func testPunycodeMatchesRfc3492() {
        XCTAssertEqual(Punycode.encode("пример"), "e1afmkfd")
        XCTAssertEqual(Punycode.encode("рф"), "p1ai")
        XCTAssertEqual(Punycode.encode("bücher"), "bcher-kva")
        XCTAssertEqual(Punycode.encode("日本語"), "wgv71a119e")
    }

    func testIpv6IsWrittenShort() throws {
        let cases = [
            ("2001:db8:0:0:0:0:0:1", "2001:db8::1"), ("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1"), ("0:0:0:0:0:0:0:1", "::1"),
            ("2001:db8:1:2:3:4:5:6", "2001:db8:1:2:3:4:5:6"), ("fe80:0:0:0:0:0:0:0", "fe80::"), ("2001:0:0:1:0:0:0:1", "2001:0:0:1::1"),
        ]
        for (full, short) in cases {
            XCTAssertEqual(AddressText.of(try XCTUnwrap(IpAddress.parse(full))), short, full)
        }
    }

    func testOnlyAPublicAddressIsWorthAskingAbout() {
        let cases = [
            ("1.1.1.1", true), ("9.9.9.9", true), ("2606:4700:4700::1111", true), ("::ffff:1.1.1.1", true), ("11.0.0.1", true), ("100.128.0.1", true),
            ("172.32.0.1", true), ("223.255.255.254", true), ("0.0.0.0", false), ("10.1.2.3", false), ("100.64.0.1", false), ("127.0.0.1", false),
            ("169.254.1.1", false), ("172.16.0.1", false), ("172.31.255.254", false), ("192.0.0.8", false), ("192.0.2.1", false), ("192.168.1.1", false),
            ("198.18.0.1", false), ("198.51.100.7", false), ("203.0.113.24", false), ("224.0.0.1", false), ("255.255.255.255", false), ("::1", false),
            ("::ffff:10.0.0.1", false), ("64:ff9b::101:101", false), ("fe80::1", false), ("fd00::1", false), ("ff02::1", false), ("2001:db8::1", false),
            ("2001:0:4136:e378::1", false), ("3fff::1", false),
        ]
        for (address, expected) in cases {
            XCTAssertEqual(PublicAddress.isPublic(address), expected, address)
        }
    }

    func testExternalIpComesFromTheTraceAnswer() {
        XCTAssertEqual(PublicAddress.traceIp("fl=1\nh=1.1.1.1\nip=203.0.113.24\nts=1\n"), "203.0.113.24")
        XCTAssertEqual(PublicAddress.traceIp("ip=2001:0db8::7\n"), "2001:db8::7")
        XCTAssertNil(PublicAddress.traceIp("ip=example.com\n"), "только адрес")
        XCTAssertNil(PublicAddress.traceIp("<html>"))
        XCTAssertNil(PublicAddress.traceIp(nil))
    }

    private let overview = """
    {"messages":[],"data_call_name":"prefix-overview","data":{"is_less_specific":false,"announced":true,
     "asns":[{"asn":64500,"holder":"EXAMPLE-AS - Example Hosting B.V."}],"related_prefixes":[],"resource":"203.0.113.0/24",
     "type":"prefix","block":{"resource":"203.0.0.0/8","desc":"APNIC (Status: ALLOCATED)","name":"IANA IPv4 Address Space Registry"},
     "actual_num_related":0,"query_time":"2026-10-26T12:00:00","num_filtered_out":0},"status":"ok","status_code":200}
    """

    private let geo = """
    {"messages":[],"data_call_name":"maxmind-geo-lite","data":{"located_resources":[{"resource":"203.0.113.0/24",
     "locations":[{"country":"NL","city":"Amsterdam","resources":["203.0.113.0/24"],"latitude":52.37,"longitude":4.89,"covered_percentage":100.0}]}],
     "result_time":"2026-10-01T00:00:00"},"status":"ok","status_code":200}
    """

    private func info(_ asn: Int?, _ holder: String?, _ prefix: String?, _ country: String?, _ city: String?) -> AddressInfo {
        AddressInfo(address: "203.0.113.24", asn: asn, holder: holder, prefix: prefix, country: country, city: city, failure: nil)
    }

    func testRipestatAnswersGiveTheProviderNetworkAndPlace() {
        let address = "203.0.113.24"
        let holder = "EXAMPLE-AS - Example Hosting B.V."
        XCTAssertEqual(PublicAddress.parse(address, overview: overview, geo: geo), info(64500, holder, "203.0.113.0/24", "NL", "Amsterdam"))
        XCTAssertEqual(
            PublicAddress.parse(address, overview: #"{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.24"}}"#, geo: geo),
            info(nil, nil, nil, "NL", "Amsterdam"), "сеть не объявлена — ни провайдера, ни сети, а место есть"
        )
        XCTAssertEqual(
            PublicAddress.parse(
                address, overview: overview,
                geo: #"{"status":"ok","data":{"located_resources":[{"resource":"203.0.113.0/24","locations":[{"country":"??","city":""},{"country":"de","city":" "}]}]}}"#
            ),
            info(64500, holder, "203.0.113.0/24", "DE", nil), "«??» — не страна, пустой город — не город"
        )
        XCTAssertNil(PublicAddress.parse(address, overview: overview, geo: nil).country, "место не ответило — провайдер все равно есть")
        XCTAssertNil(
            PublicAddress.parse(address, overview: #"{"status":"ok","data":{"announced":false,"asns":[],"resource":"203.0.113.0/24"}}"#, geo: geo).prefix,
            "сеть — только объявленная"
        )
        XCTAssertEqual(
            PublicAddress.parse(address, overview: #"{"status":"ok","data":{"announced":true,"asns":[{"asn":64500}],"resource":"203.0.113.24"}}"#, geo: nil),
            info(64500, nil, nil, nil, nil), "сам адрес вместо сети — не сеть; владельца нет — только номер"
        )
    }

    func testWithoutAnswersOrWithoutFactsTheReasonIsNamed() {
        let address = "203.0.113.24"
        XCTAssertEqual(PublicAddress.parse(address, overview: nil, geo: nil).failure, .unreachable)
        XCTAssertEqual(PublicAddress.parse(address, overview: "<html>", geo: "<html>").failure, .notFound, "ответ был, но не о том")
        XCTAssertEqual(PublicAddress.parse(address, overview: #"{"status":"error","data":{"asns":[{"asn":64500}]}}"#, geo: nil).failure, .notFound)
        XCTAssertEqual(
            PublicAddress.parse(
                address, overview: #"{"status":"ok","data":{"announced":true,"asns":[{"asn":0}],"resource":"203.0.113.0/24"}}"#,
                geo: #"{"status":"ok","data":{"located_resources":[]}}"#
            ).failure,
            .notFound
        )
    }

    func testRequestsCarryOnlyTheCanonicalAddress() {
        XCTAssertEqual(PublicAddress.requests("2606:4700:4700:0:0:0:0:1111"), [
            "https://stat.ripe.net/data/prefix-overview/data.json?resource=2606:4700:4700::1111&sourceapp=private-gateway",
            "https://stat.ripe.net/data/maxmind-geo-lite/data.json?resource=2606:4700:4700::1111&sourceapp=private-gateway",
        ])
        XCTAssertEqual(PublicAddress.requests("example.com"), [], "не адрес — не спрашивать")
    }

    func testDefaultNetworkComesFirstThenVpnWifiCellularAndTheRestByName() {
        let list = [
            PhoneNetwork(kind: .other, name: "dummy0", isDefault: false), PhoneNetwork(kind: .cellular, name: "pdp_ip1", isDefault: false),
            PhoneNetwork(kind: .wifi, name: "en0", isDefault: false), PhoneNetwork(kind: .cellular, name: "pdp_ip0", isDefault: false),
            PhoneNetwork(kind: .vpn, name: "utun3", isDefault: true), PhoneNetwork(kind: .ethernet, name: nil, isDefault: false),
        ]
        XCTAssertEqual(PhoneNetworks.order(list).map(\.name), ["utun3", "en0", "pdp_ip0", "pdp_ip1", nil, "dummy0"])
        let withoutVpn = list.filter { $0.kind != .vpn }.map { network -> PhoneNetwork in
            var copy = network
            copy.isDefault = network.name == "pdp_ip1"
            return copy
        }
        XCTAssertEqual(PhoneNetworks.order(withoutVpn).first?.name, "pdp_ip1", "сеть по умолчанию — первой, какого бы вида ни была")
    }

    func testProbeSummaryFollowsTheDesktopRules() {
        let tls = TlsFacts(protocol: "TLSv1.3", cipher: "TLS_AES_128_GCM_SHA256", alpn: "h2", subject: "example.com", issuer: "Example CA", notBefore: nil,
                           notAfter: nil, names: ["example.com"], problems: [])
        XCTAssertEqual(ProbeResult(kind: .tls, target: "example.com", port: 443, steps: [ProbeStep(index: 1, address: "203.0.113.1", timeMs: 40, outcome: .ok, tls: tls)])
            .summary().arguments, ["TLSv1.3"])
        var bad = tls
        bad.problems = [.expired]
        XCTAssertEqual(ProbeResult(kind: .tls, target: "example.com", port: 443, steps: [ProbeStep(index: 1, address: nil, timeMs: 40, outcome: .ok, tls: bad)])
            .summary().key, "probe.summary.tls.problems")
        XCTAssertEqual(ProbeResult(kind: .tls, target: "example.com", port: 443, steps: [ProbeStep(index: 1, address: nil, timeMs: 40, outcome: .ok)]).summary().key,
                       "probe.summary.failed", "TLS без сведений о рукопожатии — не удался")
        let tcp = ProbeResult(kind: .tcp, target: "example.com", port: 8443, steps: [ProbeStep(index: 1, address: nil, timeMs: 12, outcome: .ok)]).summary()
        XCTAssertEqual(tcp.key, "probe.summary.tcp")
        XCTAssertEqual(tcp.arguments, ["8443", "12"])
        XCTAssertEqual(ProbeResult(kind: .tcp, target: "example.com", port: 81, steps: [ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .refused)])
            .summary().arguments, ["81"])
        XCTAssertEqual(ProbeResult(kind: .tcp, target: "example.com", port: 81, steps: [ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .timedOut)])
            .summary().key, "probe.summary.timedOut")
        XCTAssertEqual(ProbeResult(kind: .tcp, target: "x.example", port: 81, steps: [ProbeStep(index: 1, address: nil, timeMs: nil, outcome: .notFound)])
            .summary().key, "probe.summary.notFound")
        let dns = ProbeResult(kind: .dns, target: "example.com", port: nil, steps: [
            ProbeStep(index: 1, address: "203.0.113.1", timeMs: 9, outcome: .ok), ProbeStep(index: 2, address: "2001:db8::1", timeMs: 9, outcome: .ok),
        ]).summary()
        XCTAssertEqual(dns.key, "probe.summary.dns")
        XCTAssertEqual(dns.arguments, ["2", "9"])
        XCTAssertEqual(ProbeResult(kind: .dns, target: "example.com", port: nil, steps: []).summary().key, "probe.summary.failed")
    }

    func testEverySummaryKeyHasATextInBothLanguages() throws {
        let keys = ["probe.summary.failed", "probe.summary.notFound", "probe.summary.dns", "probe.summary.tls.problems", "probe.summary.tls", "probe.summary.tcp",
                    "probe.summary.refused", "probe.summary.timedOut", "probe.summary.unreachable"]
        let localization = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/localization")
        for language in ["ru", "en"] {
            let texts = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: localization.appendingPathComponent("\(language).json"))) as? [String: Any])
            XCTAssertEqual(keys.filter { texts[$0] == nil }, [], language)
        }
    }
}
