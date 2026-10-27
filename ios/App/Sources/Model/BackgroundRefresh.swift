import BackgroundTasks
import Foundation
import PrivateGatewayCore

/// Обновление подписок, когда окна нет: iOS сама будит приложение (`BGAppRefreshTask`) — не раньше чем через час и когда
/// сочтет нужным. Защита подключена — по состоянию VPN в системе: с «только через VPN» без нее ничего не грузится. Загрузка
/// идет из процесса приложения — его соединения iOS пускает в туннель.
enum BackgroundRefresh {
    /// Задача в Info.plist (`BGTaskSchedulerPermittedIdentifiers`, `ios/project.yml`).
    static let task = "com.privategateway.client.refresh"

    /// Попросить iOS разбудить приложение; повторная просьба заменяет прежнюю.
    static func schedule() {
        let request = BGAppRefreshTaskRequest(identifier: task)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 60 * 60)
        try? BGTaskScheduler.shared.submit(request)
    }

    /// Фоновое обновление: следующее — заранее, затем подписки, которым пора. Сообщения о выбранном сервере — что вернулось.
    @discardableResult
    static func run() async -> [SubscriptionNotice] {
        schedule()
        let container = SharedContainer.current()
        let connected = await TunnelController.systemConnected()
        let updater = SubscriptionUpdater(source: SubscriptionDownloader(), servers: container.servers, settings: container.settings)
        return await updater.updateDue(connected: connected)
    }
}
