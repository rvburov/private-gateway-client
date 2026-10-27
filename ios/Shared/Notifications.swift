import Foundation
import PrivateGatewayCore
import PrivateGatewayTheme
import UserNotifications

/// Уведомления iOS — общее у приложения и расширения туннеля (файл входит в обе цели, `ios/project.yml`): текст на языке
/// приложения, кнопки (категории `UNNotificationCategory`), показ и снятие по метке. Какие показывать — решают
/// `NotificationPolicy` и `NoticeDesk` общего пакета.
enum Notifications {
    /// Ключ метки и кнопок в `userInfo` — по ним приложение решает, куда вести нажатие.
    static let tagKey = "tag"

    private static var center: UNUserNotificationCenter { .current() }

    /// Заголовок и текст, как `ProtectionNotifier` Android: {0} — сервер, {1} — подписка, {2} — причина: код ошибки словами и
    /// сам код («Ядро не запустилось · PGC-SVC-002») или почему подписка не загрузилась.
    static func words(_ notice: Notice, _ texts: PgTexts) -> (title: String, body: String) {
        var reason = ""
        if let code = notice.code {
            reason = (texts.text(key: TextKeys.error(code)) ?? "") + " · " + code
        } else if let failure = notice.failure {
            let key = TextKeys.failure(failure)
            reason = texts.text(key: key.key, key.value.map { [$0] } ?? []) ?? ""
        }

        let body = texts.text(key: notice.textKey, [notice.server ?? texts.notifyServerNone, notice.subscription ?? "", reason]) ?? ""
        return (texts.text(key: notice.kind.titleKey) ?? "", body)
    }

    /// Категория — набор кнопок уведомления: «changeServer+open», без кнопок — «plain».
    static func category(_ actions: [NoticeAction]) -> String {
        actions.isEmpty ? "plain" : actions.map(\.rawValue).joined(separator: "+")
    }

    /// Все наборы кнопок, которые дает `NotificationPolicy` (тест сверяет).
    static let actionSets: [[NoticeAction]] = [
        [.changeServer, .open], [.retry, .chooseServer], [.open], [], [.connect], [.chooseOther, .later], [.chooseServer, .later], [.openSubscriptions],
    ]

    /// Наборы кнопок с надписями на языке приложения. «Позже» только закрывает уведомление, остальные открывают окно — там и
    /// выполняется действие.
    static func register(_ texts: PgTexts) {
        let categories = actionSets.map { actions in
            UNNotificationCategory(
                identifier: category(actions),
                actions: actions.map { action in
                    UNNotificationAction(
                        identifier: action.rawValue, title: texts.text(key: action.textKey) ?? action.rawValue,
                        options: action == .later ? [] : [.foreground]
                    )
                },
                intentIdentifiers: []
            )
        }
        center.setNotificationCategories(Set(categories))
    }

    /// Показать: о защите — одно, последнее (метка — идентификатор, новое заменяет прежнее), у каждой подписки — свое.
    /// Важные — со звуком.
    static func post(_ notice: Notice, _ texts: PgTexts) {
        let words = words(notice, texts)
        let content = UNMutableNotificationContent()
        content.title = words.title
        content.body = words.body
        content.threadIdentifier = notice.tag
        content.categoryIdentifier = category(notice.actions)
        content.userInfo = [tagKey: notice.tag]
        if notice.important {
            content.sound = .default
        }

        center.add(UNNotificationRequest(identifier: notice.tag, content: content, trigger: nil))
    }

    /// Убрать уведомление о защите: беда прошла или сменилась другой.
    static func withdrawProtection() {
        center.removeDeliveredNotifications(withIdentifiers: [Notice.protectionTag])
    }

    /// Разрешение на уведомления — спрашивается при первом включении защиты, если уведомления не выключены в настройках.
    static func requestPermission() async {
        _ = try? await center.requestAuthorization(options: [.alert, .sound])
    }
}

/// Язык текстов для процесса, которому iOS не говорит язык приложения (расширение туннеля): приложение пишет свой в общую
/// папку при каждом запуске; нет файла — первый из предпочитаемых языков iPhone.
extension PgTexts {
    static func shared(_ container: SharedContainer) -> PgTexts {
        let saved = container.language.read().flatMap(PgTexts.Language.init(rawValue:))
        return PgTexts(saved ?? PgTexts.language(for: Locale.preferredLanguages))
    }
}
