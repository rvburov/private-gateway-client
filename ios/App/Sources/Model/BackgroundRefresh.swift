import BackgroundTasks
import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme

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

    /// Фоновое обновление: следующее — заранее, затем подписки, которым пора. Выбранный сервер пропал или изменился, подписка
    /// не обновилась — уведомление по уровню из настроек (окна нет — его некому показать).
    static func run() async {
        schedule()
        let container = SharedContainer.current()
        let connected = await TunnelController.systemConnected()
        let updater = SubscriptionUpdater(source: SubscriptionDownloader(), servers: container.servers, settings: container.settings)
        let notices = await updater.updateDue(connected: connected)
        let level = (try? await container.settings.load().notifications) ?? .default
        let desk = NoticeDesk()
        let texts = PgTexts.shared(container)
        for notice in notices.compactMap(NotificationPolicy.forSubscription) {
            if let show = desk.deliver(notice, now: 0, windowVisible: false, level: level).show {
                Notifications.post(show, texts)
            }
        }
    }
}
