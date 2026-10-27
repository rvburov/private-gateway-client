import Foundation

/// Когда — для подписей «обновлено в 18:10», «вчера», «22 окт.».
public enum Day: Equatable, Sendable {
    /// Сегодня — время с точностью до минуты (when.at, «HH:mm»).
    case today(hour: Int, minute: Int)
    /// Вчера (when.yesterday).
    case yesterday
    /// Раньше (или позже — часы перевели назад) — дата («d MMM» на языке интерфейса).
    case date(year: Int, month: Int, day: Int)
}

/// Сколько прошло — «только что», «5 мин назад», «2 ч назад», «3 дн назад» (ago.*).
public enum Ago: Equatable, Sendable {
    case now
    case minutes(Int)
    case hours(Int)
    case days(Int)
}

/// Время в подписях — как `TimeText` Android и десктопа: значения, а не текст, строку своего языка собирает приложение.
/// «Сейчас» и часовой пояс передаются явно — тесты ставят часы.
public enum TimeText {
    /// Сегодня — время, вчера — «вчера», иначе — дата; сутки — по часовому поясу `zone`.
    public static func day(_ at: Date, now: Date, zone: TimeZone) -> Day {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        let parts = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: at)
        let today = calendar.startOfDay(for: now)
        let day = calendar.startOfDay(for: at)
        if day == today {
            return .today(hour: parts.hour ?? 0, minute: parts.minute ?? 0)
        }

        if let yesterday = calendar.date(byAdding: .day, value: -1, to: today), day == yesterday {
            return .yesterday
        }

        return .date(year: parts.year ?? 0, month: parts.month ?? 0, day: parts.day ?? 0)
    }

    /// Минуты, часы или дни, отброшенные вниз; меньше минуты (и время из будущего) — «только что».
    public static func ago(_ at: Date, now: Date) -> Ago {
        let seconds = Int(now.timeIntervalSince(at).rounded(.down))
        switch seconds {
        case ..<60: return .now
        case ..<3600: return .minutes(seconds / 60)
        case ..<86400: return .hours(seconds / 3600)
        default: return .days(seconds / 86400)
        }
    }
}
