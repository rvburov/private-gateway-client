import Foundation
import XCTest
@testable import PrivateGatewayCore

/// Время в подписях — те же случаи, что `TimeTextTest` Android: «обновлено в 15:42», «вчера», «2 ч назад» на доске 12.
/// Часы стоят: 24 октября 2026, 15:42:30 по Москве.
final class TimeTextTests: XCTestCase {
    private let moscow = TimeZone(identifier: "Europe/Moscow")!
    private let utc = TimeZone(identifier: "UTC")!

    private func at(_ year: Int, _ month: Int, _ day: Int, _ hour: Int, _ minute: Int, _ second: Int = 0, zone: TimeZone? = nil) -> Date {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone ?? moscow
        return calendar.date(from: DateComponents(year: year, month: month, day: day, hour: hour, minute: minute, second: second))!
    }

    private var now: Date { at(2026, 10, 24, 15, 42, 30) }

    func testTodayIsTheTimeYesterdayIsAWordEarlierIsTheDate() {
        XCTAssertEqual(TimeText.day(now, now: now, zone: moscow), .today(hour: 15, minute: 42))
        XCTAssertEqual(TimeText.day(now.addingTimeInterval(-2 * 3600), now: now, zone: moscow), .today(hour: 13, minute: 42))
        XCTAssertEqual(TimeText.day(at(2026, 10, 24, 0, 0), now: now, zone: moscow), .today(hour: 0, minute: 0))
        XCTAssertEqual(TimeText.day(at(2026, 10, 23, 23, 59), now: now, zone: moscow), .yesterday)
        XCTAssertEqual(TimeText.day(at(2026, 10, 23, 0, 0), now: now, zone: moscow), .yesterday)
        XCTAssertEqual(TimeText.day(at(2026, 10, 22, 23, 59), now: now, zone: moscow), .date(year: 2026, month: 10, day: 22))
        XCTAssertEqual(TimeText.day(now.addingTimeInterval(86400), now: now, zone: moscow), .date(year: 2026, month: 10, day: 25), "часы перевели назад — дата")
    }

    func testYesterdayStartsAtLocalMidnightNot24HoursAgo() {
        let justAfterMidnight = at(2026, 10, 24, 0, 5)
        XCTAssertEqual(TimeText.day(at(2026, 10, 23, 23, 55), now: justAfterMidnight, zone: moscow), .yesterday, "10 минут назад, но до полуночи")
        XCTAssertEqual(TimeText.day(at(2026, 10, 24, 0, 1), now: justAfterMidnight, zone: moscow), .today(hour: 0, minute: 1))
    }

    func testDaysFollowTheTimeZone() {
        // 22:30 по UTC 23 октября — это 01:30 24 октября по Москве.
        let instant = at(2026, 10, 23, 22, 30, zone: utc)
        XCTAssertEqual(TimeText.day(instant, now: now, zone: moscow), .today(hour: 1, minute: 30))
        XCTAssertEqual(TimeText.day(instant, now: now, zone: utc), .yesterday)
    }

    func testAgoCountsWholeMinutesHoursAndDays() {
        let cases: [(TimeInterval, Ago)] = [
            (0, .now), (59, .now), (60, .minutes(1)), (3599, .minutes(59)), (3600, .hours(1)), (7200, .hours(2)), (86399, .hours(23)),
            (86400, .days(1)), (277_200, .days(3)), (-120, .now),
        ]
        for (seconds, expected) in cases {
            XCTAssertEqual(TimeText.ago(now.addingTimeInterval(-seconds), now: now), expected, "\(seconds) с")
        }
    }
}
