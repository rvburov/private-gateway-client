import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Какое уведомление дает событие и что доходит до пользователя — случаи `NotificationPolicyTest`, `NoticeDeskTest` и
/// `NoticeThrottleTest` Android; блок при обрыве iOS — вместо «Блокировать подключения без VPN».
final class NoticesTests: XCTestCase {
    private func status(_ state: TunnelState, _ error: String? = nil, attempt: Int = 0) -> TunnelStatus {
        TunnelStatus(state: state, serverId: "srv-1", error: error, attempt: attempt)
    }

    private var connected: TunnelStatus { status(.connected) }

    private func change(_ before: TunnelStatus, _ after: TunnelStatus, killSwitch: Bool = false) -> Notice? {
        NotificationPolicy.forChange(before, after, server: "Нидерланды 01", killSwitch: killSwitch)
    }

    // MARK: Политика

    func testLostConnectionSaysTheInternetWaitsForTheVpn() throws {
        let lost = try XCTUnwrap(change(connected, status(.reconnecting, attempt: 1)))
        XCTAssertEqual(lost.kind, .connectionLost)
        XCTAssertEqual(lost.textKey, "notify.lost.blocked", "маршруты туннеля остаются — трафик ждет и без блока при обрыве")
        XCTAssertEqual(lost.server, "Нидерланды 01")
        XCTAssertTrue(lost.important)
        XCTAssertNil(lost.code)
        XCTAssertEqual(lost.actions, [.changeServer, .open])
        XCTAssertEqual(change(connected, status(.reconnecting), killSwitch: true)?.textKey, "notify.lost.blocked")
        XCTAssertNil(change(status(.connecting), status(.reconnecting, attempt: 1)), "еще не было защиты — не обрыв")
    }

    func testFurtherAttemptsSayNothing() {
        XCTAssertNil(change(status(.reconnecting, attempt: 1), status(.reconnecting, attempt: 2)))
        XCTAssertNil(change(status(.reconnecting, attempt: 2), status(.reconnecting, attempt: 3)))
    }

    func testServerDownAndFailuresCarryTheReason() throws {
        let gone = status(.error, TunnelErrors.serverNotResponding)
        let down = try XCTUnwrap(change(status(.reconnecting), gone, killSwitch: true))
        XCTAssertEqual(down.kind, .serverDown)
        XCTAssertEqual(down.textKey, "notify.down.blocked", "блок при обрыве держит трафик")
        XCTAssertEqual(down.actions, [.retry, .chooseServer])
        XCTAssertTrue(down.important)
        XCTAssertNil(down.code, "у «Сервер не отвечает» свой текст — код не нужен")
        XCTAssertEqual(change(status(.reconnecting), gone)?.textKey, "notify.down.direct", "без блока при обрыве трафик идет напрямую")
        XCTAssertEqual(change(connected, gone)?.kind, .serverDown, "«Не повторять» сдается сразу")
        var later = gone
        later.since = Date(timeIntervalSince1970: 42)
        XCTAssertNil(change(gone, later), "то же состояние — свежие цифры")

        let other = try XCTUnwrap(change(status(.connecting), status(.error, TunnelErrors.otherVpn), killSwitch: true))
        XCTAssertEqual(other.kind, .protectionFailed)
        XCTAssertEqual(other.textKey, "notify.failed.blocked")
        XCTAssertEqual(other.code, TunnelErrors.otherVpn)
        XCTAssertEqual(other.actions, [.open])

        let core = status(.error, TunnelErrors.coreFailed)
        let failed = try XCTUnwrap(change(status(.connecting), core))
        XCTAssertEqual(failed.kind, .protectionFailed)
        XCTAssertEqual(failed.textKey, "notify.failed.direct")
        XCTAssertEqual(failed.code, TunnelErrors.coreFailed)
        XCTAssertTrue(failed.important)

        XCTAssertEqual(change(core, status(.error, TunnelErrors.otherVpn))?.code, TunnelErrors.otherVpn, "другая ошибка — новое уведомление")
        XCTAssertEqual(change(core, gone)?.kind, .serverDown)
        XCTAssertEqual(change(status(.connecting), status(.error))?.code, Notice.unknownCode, "сбой без кода — общий код")
    }

    func testConnectAndDisconnectAreOrdinary() throws {
        let on = try XCTUnwrap(change(status(.connecting), connected))
        XCTAssertEqual(on.kind, .connected)
        XCTAssertEqual(on.textKey, "notify.on.text")
        XCTAssertFalse(on.important)
        XCTAssertEqual(on.actions, [])
        XCTAssertEqual(on.server, "Нидерланды 01")
        XCTAssertEqual(change(status(.reconnecting, attempt: 3), connected)?.textKey, "notify.on.back")
        var fresh = connected
        fresh.since = Date(timeIntervalSince1970: 42)
        XCTAssertNil(change(connected, fresh), "свежие цифры — не событие")

        let off = try XCTUnwrap(change(connected, status(.off)))
        XCTAssertEqual(off.kind, .disconnected)
        XCTAssertEqual(off.textKey, "notify.off.text")
        XCTAssertEqual(off.actions, [.connect])
        XCTAssertFalse(off.important)
        XCTAssertNil(off.code)
        XCTAssertEqual(change(status(.stopping), status(.off))?.kind, .disconnected)
        XCTAssertEqual(change(status(.reconnecting), status(.off))?.kind, .disconnected)
        XCTAssertNil(change(status(.off), status(.off)))
    }

    func testGoingThroughConnectingAndStoppingSaysNothing() {
        XCTAssertNil(change(status(.off), status(.connecting)))
        XCTAssertNil(change(connected, status(.stopping)))
        XCTAssertNil(change(status(.error, TunnelErrors.coreFailed), status(.connecting)))
    }

    func testProtectionNoticesShareOneTagAndSubscriptionsHaveTheirOwn() {
        XCTAssertEqual(Notice(.connectionLost, "k", []).tag, Notice.protectionTag)
        XCTAssertEqual(Notice(.connected, "k", []).tag, Notice.protectionTag)
        XCTAssertEqual(Notice(.protectionFailed, "k", [], subscription: "Основная").tag, Notice.protectionTag)
        XCTAssertEqual(Notice(.serverGone, "k", [], subscription: "Основная").tag, "servers-Основная")
        XCTAssertEqual(Notice(.serverChanged, "k", [], subscription: "Основная").tag, "servers-Основная")
        XCTAssertEqual(Notice(.serversNotUpdated, "k", [], subscription: "Основная").tag, "servers-Основная")
        XCTAssertFalse(Notice(.serversNotUpdated, "k", [], subscription: "Основная").important)
        XCTAssertEqual(Set(NoticeKind.allCases.filter(\.important)), [.connectionLost, .serverDown, .protectionFailed])
    }

    func testSubscriptionChangesOfTheSelectedServer() throws {
        let gone = try XCTUnwrap(NotificationPolicy.forSelected(.missing, server: "Германия 02", subscription: "Основная"))
        XCTAssertEqual(gone.kind, .serverGone)
        XCTAssertEqual(gone.textKey, "notify.gone.text")
        XCTAssertEqual(gone.actions, [.chooseOther, .later])
        XCTAssertEqual(gone.server, "Германия 02")
        XCTAssertEqual(gone.subscription, "Основная")

        let changed = try XCTUnwrap(NotificationPolicy.forSelected(.changed, server: "Германия 02", subscription: "Основная"))
        XCTAssertEqual(changed.kind, .serverChanged)
        XCTAssertEqual(changed.textKey, "notify.changed.text")
        XCTAssertEqual(changed.actions, [.chooseServer, .later])
        XCTAssertNil(NotificationPolicy.forSelected(.none, server: "Германия 02", subscription: "Основная"))

        let stale = NotificationPolicy.forStale("Основная", .forbidden(403))
        XCTAssertEqual(stale.kind, .serversNotUpdated)
        XCTAssertEqual(stale.textKey, "notify.stale.text")
        XCTAssertEqual(stale.actions, [.openSubscriptions])
        XCTAssertEqual(stale.subscription, "Основная")
        XCTAssertEqual(stale.failure, .forbidden(403))
        XCTAssertNil(stale.server)

        XCTAssertEqual(NotificationPolicy.forSubscription(.gone(server: "A", subscription: "B"))?.kind, .serverGone)
        XCTAssertEqual(NotificationPolicy.forSubscription(.changed(server: "A", subscription: "B"))?.kind, .serverChanged)
        XCTAssertEqual(NotificationPolicy.forSubscription(.stale(subscription: "B", reason: .timeout))?.failure, .timeout)
    }

    func testLevelDecidesWhatIsShown() {
        let lost = Notice(.connectionLost, "k", [])
        let off = Notice(.disconnected, "k", [])
        let stale = NotificationPolicy.forStale("Основная", .timeout)
        XCTAssertTrue(NotificationPolicy.allowed(.importantOnly, lost))
        XCTAssertFalse(NotificationPolicy.allowed(.importantOnly, off), "«Только важные»: отключение — обычное")
        XCTAssertFalse(NotificationPolicy.allowed(.importantOnly, stale))
        XCTAssertTrue(NotificationPolicy.allowed(.all, off))
        XCTAssertTrue(NotificationPolicy.allowed(.all, stale))
        XCTAssertFalse(NotificationPolicy.allowed(.off, lost))
        XCTAssertFalse(NotificationPolicy.allowed(.off, off))
    }

    func testLevelIsImportantOnlyByDefault() {
        XCTAssertEqual(NotificationLevel.default, .importantOnly)
        XCTAssertEqual(NotificationLevel.read(nil), .importantOnly, "ничего не сохранено")
        for level in NotificationLevel.allCases {
            XCTAssertEqual(NotificationLevel.read(level.rawValue), level)
        }

        for name in ["", "all", "Silent", "2"] {
            XCTAssertEqual(NotificationLevel.read(name), .importantOnly, name)
        }
    }

    func testEveryKeyHasATextInBothLanguages() throws {
        var keys = Set(NoticeKind.allCases.map(\.titleKey) + NoticeAction.allCases.map(\.textKey))
        let states = TunnelState.allCases.flatMap { state in [nil, TunnelErrors.coreFailed, TunnelErrors.serverNotResponding].map { status(state, $0) } }
        for before in states {
            for after in states {
                for killSwitch in [false, true] {
                    if let notice = change(before, after, killSwitch: killSwitch) {
                        keys.insert(notice.textKey)
                    }
                }
            }
        }

        for change in [SelectedChange.none, .missing, .changed] {
            if let notice = NotificationPolicy.forSelected(change, server: "s", subscription: "g") {
                keys.insert(notice.textKey)
            }
        }

        keys.insert(NotificationPolicy.forStale("g", .timeout).textKey)
        XCTAssertTrue(keys.isSuperset(of: ["notify.lost.blocked", "notify.down.blocked", "notify.down.direct", "notify.failed.blocked", "notify.failed.direct"]))
        XCTAssertEqual(NoticeAction.openSubscriptions.textKey, "notify.action.openSubscriptions")
        XCTAssertEqual(NoticeAction.changeServer.textKey, "notify.action.changeServer")
        let localization = LinkParserTests.testData.deletingLastPathComponent().appendingPathComponent("specifications/localization")
        for language in ["ru", "en"] {
            let data = try Data(contentsOf: localization.appendingPathComponent("\(language).json"))
            let texts = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
            XCTAssertEqual(keys.filter { texts[$0] == nil }.sorted(), [], language)
        }
    }

    // MARK: Повтор

    private let start = 1_000_000
    private let lost = Notice(.connectionLost, "notify.lost.blocked", [], server: "Нидерланды 01")

    func testDropIsNotRepeatedWithinTenMinutes() {
        let throttle = NoticeThrottle()
        XCTAssertTrue(throttle.allows(lost, now: start))
        throttle.shown(lost, now: start)
        XCTAssertFalse(throttle.allows(lost, now: start), "то же событие сразу")
        var other = lost
        other.server = "Германия 02"
        XCTAssertFalse(throttle.allows(other, now: start + 60000), "другой сервер — то же событие")
        XCTAssertFalse(throttle.allows(lost, now: start + NoticeThrottle.repeatAfter - 1))
        XCTAssertTrue(throttle.allows(lost, now: start + NoticeThrottle.repeatAfter))
        XCTAssertEqual(NoticeThrottle.repeatAfter, 10 * 60 * 1000)
    }

    func testPauseCountsFromTheLastShown() {
        let throttle = NoticeThrottle()
        throttle.shown(lost, now: start)
        let later = start + NoticeThrottle.repeatAfter
        throttle.shown(lost, now: later)
        XCTAssertFalse(throttle.allows(lost, now: later + 1))
        XCTAssertTrue(throttle.allows(lost, now: later + NoticeThrottle.repeatAfter))
    }

    func testOtherEventsAndOtherSubscriptionsAreNotHeld() {
        let throttle = NoticeThrottle()
        let stale = NotificationPolicy.forStale("Основная", .timeout)
        throttle.shown(lost, now: start)
        throttle.shown(stale, now: start)
        XCTAssertTrue(throttle.allows(Notice(.connected, "notify.on.text", []), now: start), "другое событие")
        var spare = stale
        spare.subscription = "Запасная"
        XCTAssertTrue(throttle.allows(spare, now: start), "другая подписка")
        var none = stale
        none.subscription = nil
        XCTAssertTrue(throttle.allows(none, now: start), "без подписки — свой ключ")
        var tls = stale
        tls.failure = .tls
        XCTAssertFalse(throttle.allows(tls, now: start), "та же подписка, другая причина — то же событие")
    }

    // MARK: Доставка

    private final class DeskRig {
        let desk = NoticeDesk()
        var now = 1_000_000
        var before = TunnelStatus(state: .connected, serverId: "nl")
        var window = false
        var level = NotificationLevel.importantOnly
        var shown: [NoticeKind] = []
        var withdrawn = 0

        func move(_ state: TunnelState, _ error: String? = nil) {
            let after = TunnelStatus(state: state, serverId: "nl", error: error)
            let notice = NotificationPolicy.forChange(before, after, server: "Нидерланды 01", killSwitch: false)
            before = after
            if let notice {
                deliver(notice)
            }
        }

        func deliver(_ notice: Notice) {
            let delivery = desk.deliver(notice, now: now, windowVisible: window, level: level)
            if let show = delivery.show {
                shown.append(show.kind)
            }

            if delivery.withdrawProtection {
                withdrawn += 1
            }
        }
    }

    func testDropIsShownOnceWithinTenMinutes() {
        let rig = DeskRig()
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown, [.connectionLost])
        rig.move(.connected)
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown.count, 1, "повтор того же события — не чаще раза в 10 минут")
        rig.now += NoticeThrottle.repeatAfter
        rig.move(.connected)
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown.count, 2)
    }

    func testWindowOnTheScreenGetsNothingAndTheEventCountsAsShown() {
        let rig = DeskRig()
        rig.window = true
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown, [], "окно показывает «Переподключение» само")
        rig.window = false
        rig.move(.connected)
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown, [], "событие при открытом окне считается показанным: повтора нет 10 минут")
    }

    func testDeskLevelDecidesWhatIsShown() {
        let rig = DeskRig()
        rig.move(.off)
        XCTAssertEqual(rig.shown, [], "«Только важные» (по умолчанию): отключение — обычное")
        rig.level = .all
        rig.move(.connecting)
        rig.move(.connected)
        XCTAssertEqual(rig.shown, [.connected], "окно скрыто — «Подключено» видно")
        rig.window = true
        rig.move(.off)
        XCTAssertEqual(rig.shown.count, 1, "окно на экране — «VPN отключен» и так видно")
        rig.level = .off
        rig.window = false
        rig.move(.connected)
        rig.move(.reconnecting)
        XCTAssertEqual(rig.shown.count, 1, "выключены — ничего")
    }

    func testDropThatPassedIsTakenAwayOnce() {
        let rig = DeskRig()
        rig.move(.reconnecting)
        rig.move(.connected)
        XCTAssertEqual(rig.withdrawn, 1, "«Только важные»: «Подключено» не показано — «Соединение потеряно» убрано")
        rig.move(.off)
        rig.move(.connected)
        rig.move(.reconnecting)
        XCTAssertEqual(rig.withdrawn, 1, "убирать уже нечего")
    }

    func testRepeatOfTheSameKindDoesNotTakeTheShownOneAway() throws {
        let lost = try XCTUnwrap(NotificationPolicy.forChange(TunnelStatus(state: .connected), TunnelStatus(state: .reconnecting), server: "Германия 02", killSwitch: false))
        let desk = NoticeDesk()
        _ = desk.deliver(lost, now: start, windowVisible: false, level: .importantOnly)
        let repeated = desk.deliver(lost, now: start + 1, windowVisible: false, level: .importantOnly)
        XCTAssertNil(repeated.show)
        XCTAssertFalse(repeated.withdrawProtection, "тот же вид показан — его и показывает повтор, убирать нечего")
    }

    func testPressedNoticeIsNoLongerShown() {
        let rig = DeskRig()
        rig.move(.reconnecting)
        rig.desk.protectionGone()
        rig.move(.connected)
        XCTAssertEqual(rig.withdrawn, 0, "нажатое уведомление уже убрано")
    }

    func testSubscriptionNoticesFollowTheLevelAndNeverTakeTheProtectionNoticeAway() throws {
        let rig = DeskRig()
        rig.move(.reconnecting)
        let stale = NotificationPolicy.forStale("Основная", .status(503))
        rig.deliver(stale)
        XCTAssertEqual(rig.shown, [.connectionLost], "обычное при «Только важные»")
        XCTAssertEqual(rig.withdrawn, 0, "у подписки своя метка — уведомление о защите не трогается")
        rig.level = .all
        rig.deliver(try XCTUnwrap(NotificationPolicy.forSelected(.missing, server: "Германия 02", subscription: "Основная")))
        XCTAssertEqual(rig.shown.last, .serverGone)
        rig.window = true
        rig.now += NoticeThrottle.repeatAfter
        let delivery = rig.desk.deliver(stale, now: rig.now, windowVisible: true, level: .all)
        XCTAssertNil(delivery.show, "окно на экране — уведомлений нет при любом уровне")
        XCTAssertFalse(delivery.withdrawProtection)
    }
}
