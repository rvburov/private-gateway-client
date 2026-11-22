import Foundation
import Darwin
import PrivateGatewayCore

/// Значения заголовков известны до вызова libXray. Диагностика декодируется и маскируется до возврата другим обработчикам,
/// чтобы JSON-экранирование не превратило неизвестный заголовок из extra в открытый секрет.
enum NativeCorePrivacy {
    /// Встроенный pingBatch не принимает LogConfig и может писать в default console до установки своего logger.
    /// Дескрипторы процесса перенаправляются один раз и никогда не восстанавливаются: поздний Go writer не получит
    /// исходный stdout/stderr. Полезная диагностика идет через обезличенный ответ и собственный private error pipe.
    private static let consoleReady = installConsoleSink(
        openSink: { Darwin.open("/dev/null", O_WRONLY) }, duplicate: { Darwin.dup2($0, $1) }, closeSink: { _ = Darwin.close($0) }
    )

    static func installConsoleSink(
        openSink: () -> Int32, duplicate: (Int32, Int32) -> Int32, closeSink: (Int32) -> Void
    ) -> Bool {
        let descriptor = openSink()
        guard descriptor >= 0 else { return false }
        defer { if descriptor != STDOUT_FILENO && descriptor != STDERR_FILENO { closeSink(descriptor) } }
        guard duplicate(descriptor, STDOUT_FILENO) >= 0 else { return false }
        return duplicate(descriptor, STDERR_FILENO) >= 0
    }

    static func invoke(_ request: String, call: (String) -> String?) -> String? {
        invoke(request, ready: consoleReady, call: call)
    }

    static func invoke(_ request: String, ready: Bool, call: (String) -> String?) -> String? {
        guard ready else { return #"{"success":false,"error":"Native console privacy unavailable"}"# }
        return call(request)
    }

    static func headerValues(in request: String) -> [String] {
        guard let root = try? JSONSerialization.jsonObject(with: Data(request.utf8)) else { return [] }
        var found: Set<String> = []
        func visit(_ node: Any, depth: Int) {
            guard depth < 32 else { return }
            if let fields = node as? [String: Any] {
                for (key, value) in fields {
                    if key.lowercased() == "headers", let headers = value as? [String: Any] {
                        for header in headers.values {
                            if let text = header as? String, !text.isEmpty { found.insert(text) }
                            if let values = header as? [String] { found.formUnion(values.filter { !$0.isEmpty }) }
                        }
                    }
                    if key == "xrayJson", let json = value as? String,
                       let nested = try? JSONSerialization.jsonObject(with: Data(json.utf8)) {
                        visit(nested, depth: depth + 1)
                    } else { visit(value, depth: depth + 1) }
                }
            } else if let values = node as? [Any] {
                values.forEach { visit($0, depth: depth + 1) }
            }
        }
        visit(root, depth: 0)
        return found.sorted()
    }

    /// Коды, времена и успех сохраняются. Ошибки любого уровня — только в обезличенном виде; не-JSON не передается дальше.
    static func response(_ response: String?, knownSecrets: [String]) -> String? {
        guard let response, let root = try? JSONSerialization.jsonObject(with: Data(response.utf8)) else { return nil }
        func sanitized(_ node: Any) -> Any {
            if let fields = node as? [String: Any] { return fields.mapValues { sanitized($0) } }
            if let values = node as? [Any] { return values.map { sanitized($0) } }
            if let text = node as? String { return SecretMasker.mask(text, knownSecrets: knownSecrets) }
            return node
        }
        guard let data = try? JSONSerialization.data(withJSONObject: sanitized(root)) else { return nil }
        return String(decoding: data, as: UTF8.self)
    }
}
