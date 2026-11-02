import Foundation
import PrivateGatewayCore
import UserNotifications

/// Уведомления в приложении: окно на экране — не в шторку, а подсказкой сверху «Заголовок · текст» на 6 с (доска 19, как окно
/// десктопа): событие о защите видно и не на главной; нажатие на уведомление или его кнопку — модели окна. Нажатие до того,
/// как окно готово (запуск из уведомления), ждет его.
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
        let content = notification.request.content
        let text = content.body.isEmpty ? content.title : content.title + " · " + content.body
        await shown(text)
        return []
    }

    private func shown(_ text: String) {
        model?.showEvent(text)
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
