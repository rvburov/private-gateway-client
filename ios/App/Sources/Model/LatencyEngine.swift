import Foundation
import LibXray
import os
import PrivateGatewayCore

/// libXray в процессе приложения — только для замера задержки (`pingBatch`): ядро туннеля живет в расширении, а замер идет
/// без защиты напрямую, а под защитой через активный туннель. Экземпляр ядра расширения при этом не меняется.
/// Ответ — как есть (разбирает `PingBatch`); отказ вызова — в журнал через
/// маскировку: в нем бывают адреса серверов.
enum LatencyEngine {
    private static let log = Logger(subsystem: "com.privategateway.client", category: "latency")

    static func ping(_ request: String) async -> String? {
        let secrets = NativeCorePrivacy.headerValues(in: request)
        return await withCheckedContinuation { (continuation: CheckedContinuation<String?, Never>) in
            DispatchQueue.global(qos: .userInitiated).async {
                let response = NativeCorePrivacy.response(NativeCorePrivacy.invoke(request) { LibXrayInvoke($0) }, knownSecrets: secrets)
                let parsed = XrayResponse.parse(response)
                if !parsed.success {
                    LatencyEngine.log.error("pingBatch: \(SecretMasker.mask(parsed.error ?? "no answer", knownSecrets: secrets), privacy: .public)")
                }

                continuation.resume(returning: response)
            }
        }
    }
}
