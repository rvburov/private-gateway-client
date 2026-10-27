import Foundation
import PrivateGatewayCore
import UserNotifications

/// Уведомления в приложении: окно на экране — не показывать (окно показывает то же само — доска 22); нажатие на уведомление
/// или его кнопку — модели окна. Нажатие до того, как окно готово (запуск из уведомления), ждет его.
@MainActor
final class AppNotifications: NSObject, UNUserNotificationCenterDelegate {
    static let shared = AppNotifications()

    private weak var model: AppModel?
    private var pending: String?

    /// Модель окна готова: отдать ей нажатие, которое пришло раньше.
    func attach(_ model: AppModel) {
        self.model = model
        if let pending {
            self.pending = nil
            model.notificationTapped(pending)
        }
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async
        -> UNNotificationPresentationOptions
    {
        []
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse) async {
        let action = response.actionIdentifier
        await tapped(action)
    }

    private func tapped(_ action: String) {
        if let model {
            model.notificationTapped(action)
        } else {
            pending = action
        }
    }
}
