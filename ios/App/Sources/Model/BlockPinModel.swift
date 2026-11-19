import Foundation
import PrivateGatewayCore

/// PIN-код блока сайтов на iOS (решение 742) — `BlockPinGate` десктопа и PIN-код `SettingsViewModel` Android: код из Связки
/// ключей (в резервную копию не уходит), проверка с паузой после пяти неверных подряд — счетчик живет, пока живо окно.
@MainActor
final class BlockPinModel: ObservableObject {
    @Published private(set) var pin: BlockPin?

    private let store: BlockPinStore
    private let attempts = BlockPinAttempts()

    init(container: SharedContainer = .current()) {
        store = container.blockPin
        pin = store.read()
    }

    func check(_ code: String) -> PinCheck {
        attempts.check(code, stored: pin)
    }

    /// Задать, сменить или снять код; Связка ключей не записала — прежний код остается.
    func set(_ next: BlockPin?) {
        do {
            try store.save(next)
            pin = next
        } catch {
            pin = store.read()
        }
    }
}
