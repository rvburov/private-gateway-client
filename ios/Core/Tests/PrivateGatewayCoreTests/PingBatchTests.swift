import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Задержка через libXray (`pingBatch`) — те же случаи, что `PingBatchTest` Android: у каждого сервера — свой конфиг из
/// одного выхода, тот же, что в туннеле; порции не больше пяти, порядок серверов сохраняется; Hysteria2 и TUIC в libXray не
/// уходят; ответ разбирается так же, как результат `LatencyProbe` десктопа: нет ответа, отказ ядра, задержка не меньше 1 мс.
final class PingBatchTests: XCTestCase {
    private let noResponse = Latency.failed(.noResponse)
    private let coreFailed = Latency.failed(.coreFailed)

    private func link(_ name: String) throws -> ServerDefinition {
        let text = try String(contentsOf: LinkParserTests.testData.appendingPathComponent("links/\(name).txt"), encoding: .utf8)
        return try XCTUnwrap(LinkParser.parse(text.trimmingCharacters(in: .whitespacesAndNewlines)).server, "ссылка \(name) не разобралась")
    }

    private func json(_ text: String) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any])
    }

    private func payload(_ request: String) throws -> [String: Any] {
        try XCTUnwrap(json(request)["payload"] as? [String: Any])
    }

    private func configs(_ request: String) throws -> [[String: Any]] {
        try XCTUnwrap(payload(request)["configs"] as? [[String: Any]])
    }

    func testPingConfigIsTheTunnelOutboundAndNothingElse() throws {
        let trojan = try link("trojan/ws")
        let settings = CoreSettings(mux: true, fingerprint: "safari")
        let config = try json(XCTUnwrap(PingBatch.outbounds(trojan, settings).json, "отказ"))

        XCTAssertEqual(Set(config.keys), ["outbounds"], "libXray читает только выходы")
        let tunnel = try XCTUnwrap((json(XCTUnwrap(XrayConfig.build(trojan, settings, tunFd: 1).json))["outbounds"] as? [Any])?.first as? [String: Any])
        let single = try XCTUnwrap((config["outbounds"] as? [Any])?.first as? [String: Any])
        XCTAssertEqual(
            try JSONSerialization.data(withJSONObject: single, options: .sortedKeys), try JSONSerialization.data(withJSONObject: tunnel, options: .sortedKeys),
            "тот же выход, что в туннеле, с настройками"
        )
        XCTAssertEqual(tunnel["tag"] as? String, "proxy")
        XCTAssertEqual((config["outbounds"] as? [Any])?.count, 1)

        XCTAssertNil(PingBatch.outbounds(try link("hysteria2/hy2-short-scheme"), CoreSettings()).json)
        XCTAssertEqual(PingBatch.outbounds(try link("hysteria2/hy2-short-scheme"), CoreSettings()).rejection, .unsupportedProtocol)
        var keyless = try link("vless/reality-vision")
        keyless.credentials.uuid = nil
        XCTAssertEqual(PingBatch.outbounds(keyless, CoreSettings()).rejection, .missingCredentials)
    }

    func testRequestIsApi3PingBatchWithConfigsTimeoutAndUrl() throws {
        XCTAssertEqual(
            PingBatch.request([#"{"outbounds":[]}"#]),
            #"{"apiVersion":3,"method":"pingBatch","payload":{"configs":[{"xrayJson":"{\"outbounds\":[]}","outboundTag":"proxy"}],"#
                + #""timeout":5,"url":"https://cp.cloudflare.com/generate_204"}}"#
        )
        let custom = try payload(XCTUnwrap(PingBatch.request(["a", "b"], url: "http://192.0.2.1/204", timeoutSeconds: 2)))
        XCTAssertEqual(custom["timeout"] as? Int, 2)
        XCTAssertEqual(custom["url"] as? String, "http://192.0.2.1/204")
        XCTAssertEqual((custom["configs"] as? [[String: Any]])?.map { $0["xrayJson"] as? String }, ["a", "b"])

        XCTAssertNil(PingBatch.request(Array(repeating: "{}", count: 6)), "больше пяти libXray не примет")
        XCTAssertNil(PingBatch.request([]))
        XCTAssertNil(PingBatch.request(["{}"], timeoutSeconds: 0), "срок в целых секундах, больше нуля")
        XCTAssertNotNil(PingBatch.request(Array(repeating: "{}", count: 5), timeoutSeconds: 1))
    }

    func testTwelveServersGoInChunksOfFiveFiveAndTwoInTheirOrder() throws {
        let reality = try link("vless/reality-vision")
        let targets = (1...12).map { index -> PingTarget in
            var server = reality
            server.port = 1000 + index
            return PingTarget(id: "s\(index)", server: server)
        }

        let plan = PingBatch.plan(targets, CoreSettings())

        XCTAssertEqual(plan.chunks.map(\.ids.count), [5, 5, 2])
        XCTAssertEqual(plan.chunks.flatMap(\.ids), targets.map(\.id))
        XCTAssertEqual(plan.unsupported, [:])
        for chunk in plan.chunks {
            let items = try configs(chunk.request)
            let ports = try items.map { item -> Int in
                let outbound = try XCTUnwrap((json(XCTUnwrap(item["xrayJson"] as? String))["outbounds"] as? [[String: Any]])?.first)
                let settings = try XCTUnwrap(outbound["settings"] as? [String: Any])
                return try XCTUnwrap((settings["vnext"] as? [[String: Any]])?.first?["port"] as? Int)
            }
            XCTAssertEqual(ports, chunk.ids.map { 1000 + Int($0.dropFirst())! })
            XCTAssertTrue(items.allSatisfy { $0["outboundTag"] as? String == "proxy" })
        }

        XCTAssertEqual(PingBatch.plan(Array(targets.prefix(5)), CoreSettings()).chunks.map(\.ids.count), [5], "ровно пять — одна порция")
        XCTAssertEqual(PingBatch.plan(Array(targets.prefix(6)), CoreSettings()).chunks.map(\.ids.count), [5, 1])
    }

    func testPlanMeasuresWithTheConnectionSettingsAndPassesUrlAndTimeout() throws {
        let plan = PingBatch.plan(
            [PingTarget(id: "tr", server: try link("trojan/ws"))], CoreSettings(fingerprint: "safari"), url: "http://192.0.2.1/204", timeoutSeconds: 3
        )

        let request = try XCTUnwrap(plan.chunks.first).request
        XCTAssertEqual(try payload(request)["timeout"] as? Int, 3)
        XCTAssertEqual(try payload(request)["url"] as? String, "http://192.0.2.1/204")
        let xrayJson = try XCTUnwrap(configs(request).first?["xrayJson"] as? String)
        let outbound = try XCTUnwrap((json(xrayJson)["outbounds"] as? [[String: Any]])?.first)
        let tls = try XCTUnwrap((outbound["streamSettings"] as? [String: Any])?["tlsSettings"] as? [String: Any])
        XCTAssertEqual(tls["fingerprint"] as? String, "safari", "отпечаток из настроек, как при подключении")
    }

    func testHysteria2AndTuicAreNotSentAndGetUnsupportedAtOnce() throws {
        let hysteria = try link("hysteria2/hy2-short-scheme")
        let plan = PingBatch.plan(
            [
                PingTarget(id: "hy", server: hysteria), PingTarget(id: "nl", server: try link("vless/reality-vision")),
                PingTarget(id: "tuic", server: try link("tuic/bbr")), PingTarget(id: "tr", server: try link("trojan/ws")),
            ],
            CoreSettings()
        )

        XCTAssertEqual(plan.chunks.map(\.ids), [["nl", "tr"]])
        XCTAssertEqual(plan.unsupported, ["hy": .failed(.unsupported), "tuic": .failed(.unsupported)])
        XCTAssertFalse(try XCTUnwrap(plan.chunks.first).request.contains("pw-hy2-2"), "Hysteria2 в запрос не попал")

        let only = PingBatch.plan([PingTarget(id: "hy", server: hysteria)], CoreSettings())
        XCTAssertTrue(only.chunks.isEmpty, "одни неподдерживаемые — libXray не нужен")
        XCTAssertEqual(Set(only.unsupported.keys), ["hy"])
        XCTAssertTrue(PingBatch.plan([], CoreSettings()).chunks.isEmpty)
    }

    func testAnswersBecomeLatencyNoResponseOrAFailedChunk() {
        let response = #"{"success":true,"data":{"results":["#
            + #"{"success":true,"delay":41},{"success":false,"delay":11000,"error":"timeout"},"#
            + #"{"success":false,"delay":10000,"error":"dial tcp 203.0.113.24:443: connection refused"},{"success":true,"delay":0}"#
            + #"]},"error":""}"#

        XCTAssertEqual(PingBatch.parse(response, 4), [.of(41), noResponse, noResponse, .of(1)])

        let busy = #"{"success":false,"data":null,"error":"pingBatch requires an isolated process without a managed Xray instance"}"#
        XCTAssertEqual(PingBatch.parse(busy, 3), Array(repeating: coreFailed, count: 3), "ядро уже запущено в процессе — замера не было")
        let failedWithResults = #"{"success":false,"data":{"results":[{"success":true,"delay":41}]},"error":"x"}"#
        XCTAssertEqual(PingBatch.parse(failedWithResults, 1), [coreFailed], "отказ вызова главнее результатов")
        XCTAssertEqual(PingBatch.parse(nil, 2), [coreFailed, coreFailed])
        XCTAssertEqual(PingBatch.parse("not json", 2), [coreFailed, coreFailed])
        XCTAssertEqual(PingBatch.parse(#"{"success":true,"data":{"results":[{"success":true,"delay":41}]}}"#, 2), [coreFailed, coreFailed])
        XCTAssertEqual(PingBatch.parse(#"{"success":true,"data":null,"error":""}"#, 1), [coreFailed])
    }

    func testErrorTextsOfServersComeOnlyThroughErrors() throws {
        let refused = "dial tcp 203.0.113.24:443: connection refused"
        let response = #"{"success":true,"data":{"results":["#
            + #"{"success":true,"delay":41,"error":""},{"success":false,"delay":11000,"error":"timeout"},"#
            + #"{"success":false,"delay":10000,"error":"\#(refused)"},{"success":false,"delay":10000}"#
            + #"]},"error":""}"#

        XCTAssertEqual(PingBatch.errors(response, 4), [nil, "timeout", refused, nil])
        XCTAssertFalse("\(PingBatch.parse(response, 4))".contains("203.0.113.24"), "адрес из ошибки — не в замере")

        let chunk = try XCTUnwrap(PingBatch.plan(
            [PingTarget(id: "a", server: try link("vless/reality-vision")), PingTarget(id: "b", server: try link("trojan/ws"))], CoreSettings()
        ).chunks.first)
        let two = #"{"success":true,"data":{"results":[{"success":true,"delay":30},{"success":false,"delay":10000,"error":"\#(refused)"}]}}"#
        XCTAssertEqual(chunk.errors(two), ["b": refused])
        XCTAssertFalse("\(chunk.results(two))".contains("203.0.113.24"))

        let busy = #"{"success":false,"data":{"results":[{"success":false,"error":"\#(refused)"}]},"error":"busy"}"#
        XCTAssertEqual(PingBatch.errors(busy, 1), [nil], "отказ вызова — в самом ответе, не у серверов")
        XCTAssertEqual(PingBatch.errors(response, 2), [nil, nil], "не столько результатов — ничего")
        XCTAssertEqual(PingBatch.errors("not json", 1), [nil])
        XCTAssertEqual(chunk.errors(nil), [:])
    }

    func testSentinelDelaysAreNeverALatencyAndTheSuccessFlagDecides() {
        func one(_ item: String) -> Latency? {
            PingBatch.parse(#"{"success":true,"data":{"results":["# + item + #"]},"error":""}"#, 1).first
        }

        XCTAssertEqual(one(#"{"success":true,"delay":11000}"#), noResponse, "тайм-аут — не 11 секунд")
        XCTAssertEqual(one(#"{"success":true,"delay":10000}"#), noResponse, "ошибка — не 10 секунд")
        XCTAssertEqual(one(#"{"success":true,"delay":9999}"#), Latency.of(9999))
        XCTAssertEqual(one(#"{"success":true,"delay":10001}"#), Latency.of(10001))
        XCTAssertEqual(one(#"{"success":false,"delay":41}"#), noResponse)
        XCTAssertEqual(one(#"{"delay":41}"#), noResponse)
        XCTAssertEqual(one(#"{"success":true}"#), noResponse)
        XCTAssertEqual(one("null"), noResponse)
    }

    func testChunkResultsKeepIds() throws {
        let reality = try link("vless/reality-vision")
        let chunk = try XCTUnwrap(PingBatch.plan(
            [PingTarget(id: "a", server: reality), PingTarget(id: "b", server: try link("trojan/ws")), PingTarget(id: "c", server: reality)],
            CoreSettings()
        ).chunks.first)
        let response = #"{"success":true,"data":{"results":[{"success":true,"delay":30},{"success":false,"delay":11000},{"success":true,"delay":7}]}}"#

        XCTAssertEqual(chunk.results(response), ["a": .of(30), "b": noResponse, "c": .of(7)])
        XCTAssertEqual(chunk.results(nil), ["a": coreFailed, "b": coreFailed, "c": coreFailed])
    }

    func testSecretsAreOnlyInsideXrayJson() throws {
        let secrets = ["11111111-2222-3333-4444-555555555555", "pw-trojan-1"]
        let plan = PingBatch.plan(
            [
                PingTarget(id: "nl", server: try link("vless/reality-vision")), PingTarget(id: "tr", server: try link("trojan/ws")),
                PingTarget(id: "hy", server: try link("hysteria2/hy2-short-scheme")),
            ],
            CoreSettings()
        )
        let request = try XCTUnwrap(plan.chunks.first).request
        let items = try configs(request)
        for secret in secrets {
            XCTAssertTrue(items.contains { ($0["xrayJson"] as? String)?.contains(secret) == true }, "ключ нужен ядру")
        }

        var root = try json(request)
        var payload = try XCTUnwrap(root["payload"] as? [String: Any])
        payload["configs"] = items.map { $0.filter { $0.key != "xrayJson" } }
        root["payload"] = payload
        let stripped = String(decoding: try JSONSerialization.data(withJSONObject: root), as: UTF8.self)
        for secret in secrets {
            XCTAssertFalse(stripped.contains(secret), "вне xrayJson ключей нет")
        }

        let texts = plan.chunks.map(\.description) + ["\(plan.unsupported)", "\(PingBatch.parse(#"{"success":false,"error":"pingBatch: 11111111-2222-3333-4444-555555555555"}"#, 2))"]
        for text in texts {
            for secret in secrets + ["pw-hy2-2"] {
                XCTAssertFalse(text.contains(secret), "в тексте порции и результатов ключей нет: \(text)")
            }
        }
        XCTAssertTrue(try XCTUnwrap(plan.chunks.first).description.contains("nl"))
    }
}
