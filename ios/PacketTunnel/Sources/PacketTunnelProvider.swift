import NetworkExtension
import PrivateGatewayCore

/// Расширение туннеля (Packet Tunnel). Ядро Xray (libXray, .xcframework) подключается следующим шагом этапа 10; до него
/// туннель честно отказывает с кодом «ядро не запустилось» (PGC-SVC-002), а не поднимается без защиты.
final class PacketTunnelProvider: NEPacketTunnelProvider {
    override func startTunnel(options: [String: NSObject]?) async throws {
        throw TunnelFailure(code: TunnelErrors.coreFailed)
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {}
}

/// Отказ туннеля с кодом ошибки (PGC-…): приложение показывает текст error.<код>.
struct TunnelFailure: LocalizedError {
    let code: String

    var errorDescription: String? { code }
}
