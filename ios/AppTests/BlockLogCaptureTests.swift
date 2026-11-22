import Darwin
import Foundation
import PrivateGatewayCore
import XCTest
@testable import PrivateGateway

final class BlockLogCaptureTests: XCTestCase {
    func testNativeInvocationIsRefusedWhenConsoleSinkCannotBeInstalled() {
        var duplicates: [Int32] = []
        var closed: [Int32] = []
        let ready = NativeCorePrivacy.installConsoleSink(openSink: { 77 }, duplicate: { _, target in
            duplicates.append(target)
            return target == STDERR_FILENO ? -1 : target
        }, closeSink: { closed.append($0) })
        XCTAssertFalse(ready)
        XCTAssertEqual(duplicates, [STDOUT_FILENO, STDERR_FILENO])
        XCTAssertEqual(closed, [77])
        var invoked = false
        let response = NativeCorePrivacy.invoke("private request", ready: ready) { _ in invoked = true; return nil }
        XCTAssertFalse(invoked)
        XCTAssertFalse(XrayResponse.parse(response).success)
    }

    func testConsoleSinkKeepsStandardDescriptorsOpenForLateNativeWriters() {
        var closed: [Int32] = []
        let ready = NativeCorePrivacy.installConsoleSink(openSink: { STDOUT_FILENO }, duplicate: { _, target in target }, closeSink: { closed.append($0) })
        XCTAssertTrue(ready)
        XCTAssertTrue(closed.isEmpty, "если stdout раньше был закрыт, новый /dev/null нельзя закрывать и отдавать другой операции")
    }

    /// Тот же open с O_APPEND|O_CREAT, которым закрепленный Xray открывает приемник error: на Darwin это отдельный
    /// дескриптор pipe, а не файл и не перенаправленный stdout процесса.
    func testNativeAccessWriterUsesThePrivatePipeWithoutReplacingStdout() async throws {
        let history = BlockedSiteHistory()
        let capture = BlockLogCapture(history: history)
        var before = stat()
        XCTAssertEqual(fstat(STDOUT_FILENO, &before), 0)
        let path = try capture.start()
        defer { capture.stop() }
        XCTAssertTrue(path.hasPrefix("/dev/fd/"))
        var during = stat()
        XCTAssertEqual(fstat(STDOUT_FILENO, &during), 0)
        XCTAssertEqual(before.st_dev, during.st_dev)
        XCTAssertEqual(before.st_ino, during.st_ino)
        let writer = open(path, O_WRONLY | O_APPEND | O_CREAT, mode_t(0o600))
        XCTAssertGreaterThanOrEqual(writer, 0)
        guard writer >= 0 else { return }
        defer { close(writer) }
        let lines = Data(("[Info] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]\n"
            + "[Info] app/dispatcher: taking detour [proxy] for [tcp:allowed.example:443]\n").utf8)
        let sent = lines.withUnsafeBytes { write(writer, $0.baseAddress, $0.count) }
        XCTAssertEqual(sent, lines.count)
        for _ in 0..<100 where history.snapshot().isEmpty {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertEqual(history.snapshot().map(\.target), ["blocked.example"])
    }

    func testInternalInfoDoesNotExposeVisitsOrIgnoreTheChosenLogLevel() throws {
        XCTAssertNil(BlockLogCapture.filteredLog("[Info] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]", requested: "debug"))
        XCTAssertNil(BlockLogCapture.filteredLog("[Info] app/dispatcher: sniffed domain: private.example", requested: "debug"))
        XCTAssertNil(BlockLogCapture.filteredLog("[Info] core: ordinary event", requested: "warning"))
        let warning = try XCTUnwrap(BlockLogCapture.filteredLog("[Warning] dial tcp:private.example:443 failed for other.example", requested: "warning"))
        XCTAssertEqual(warning.1, .warning)
        XCTAssertFalse(warning.0.contains("private.example"))
        XCTAssertFalse(warning.0.contains("other.example"))
        XCTAssertNotNil(BlockLogCapture.filteredLog("[Debug] core: ordinary event", requested: "debug"))
    }

    func testKnownExtraHeaderIsRemovedBeforeNativeLogCallbacksWithoutLosingBlockedNames() async throws {
        let secret = "opaque-client-key-987654!+/="
        let history = BlockedSiteHistory()
        let emitted = NativeMessages()
        let capture = BlockLogCapture(history: history, knownSecrets: [secret]) { message, _ in emitted.append(message) }
        let path = try capture.start()
        defer { capture.stop() }
        let writer = open(path, O_WRONLY | O_APPEND | O_CREAT, mode_t(0o600))
        XCTAssertGreaterThanOrEqual(writer, 0)
        guard writer >= 0 else { return }
        defer { close(writer) }
        let bytes = Data(("[Warning] X-Private-Key was rejected: \(secret)\n"
            + "[Info] app/dispatcher: taking detour [block] for [tcp:blocked.example:443]\n").utf8)
        _ = bytes.withUnsafeBytes { write(writer, $0.baseAddress, $0.count) }
        for _ in 0..<100 where emitted.values.isEmpty || history.snapshot().isEmpty {
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        XCTAssertFalse(emitted.values.isEmpty)
        XCTAssertFalse(emitted.values.joined().contains(secret))
        XCTAssertEqual(history.snapshot().map(\.target), ["blocked.example"])
        XCTAssertFalse(emitted.values.joined().contains("blocked.example"))
    }

    func testNativeResponsesAndPingRequestHeadersAreMaskedBeforeConsumers() throws {
        let secret = "private!key/with\"quote\\slash"
        let nested = "download-private-key-1234"
        let config: [String: Any] = ["outbounds": [["streamSettings": ["xhttpSettings": [
            "headers": ["X-Private-Key": secret], "downloadSettings": ["xhttpSettings": ["headers": ["X-Private-Download": [nested]]]],
        ]]]]]
        let configText = String(decoding: try JSONSerialization.data(withJSONObject: config), as: UTF8.self)
        let request = String(decoding: try JSONSerialization.data(withJSONObject: ["method": "pingBatch", "params": ["configs": [["xrayJson": configText]]]]), as: UTF8.self)
        let known = NativeCorePrivacy.headerValues(in: request)
        XCTAssertEqual(Set(known), Set([secret, nested]))
        let error = "TLS header X-Private-Key rejected: \(secret); nested=\(nested)"
        let response = String(decoding: try JSONSerialization.data(withJSONObject: ["success": false, "error": error]), as: UTF8.self)
        let masked = try XCTUnwrap(NativeCorePrivacy.response(response, knownSecrets: known))
        let parsed = XrayResponse.parse(masked)
        XCTAssertFalse(parsed.success)
        XCTAssertTrue(parsed.error?.contains("TLS") == true)
        XCTAssertFalse(parsed.error?.contains(secret) == true)
        XCTAssertFalse(parsed.error?.contains(nested) == true)
        XCTAssertNil(NativeCorePrivacy.response("invalid native response " + secret, knownSecrets: known))
    }
}

private final class NativeMessages: @unchecked Sendable {
    private let lock = NSLock()
    private var lines: [String] = []
    var values: [String] {
        lock.lock()
        defer { lock.unlock() }
        return lines
    }
    func append(_ message: String) {
        lock.lock()
        lines.append(message)
        lock.unlock()
    }
}
