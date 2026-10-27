import Foundation

/// Скрывает секреты в строке перед записью в журнал — как `SecretMasker` десктопа и Android, те же правила: ссылки на
/// серверы, пути и параметры адресов подписок, UUID, ключи. Журнал пользователь может отправить в отчете, поэтому в нем не
/// должно быть ничего, по чему можно подключиться к его серверу.
public enum SecretMasker {
    /// Чем заменяется скрытая часть.
    public static let hidden = "[скрыто]"

    // Ссылки серверов: все после схемы — учетные данные и параметры. Выражения ICU неизменяемы — их можно делить между
    // потоками.
    nonisolated(unsafe) private static let proxyLink = regex(#"\b(vless|vmess|trojan|ss|ssr|hysteria2|hy2|tuic|wireguard|wg)://[^\s"'<>]+"#, ignoreCase: true)

    // Адрес подписки: путь и параметры часто и есть токен доступа, хост оставляем.
    nonisolated(unsafe) private static let httpUrl = regex(#"(\bhttps?://[^\s/"'<>?#]+)([/?#][^\s"'<>]*)"#, ignoreCase: true)

    nonisolated(unsafe) private static let uuid = regex(#"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b"#, ignoreCase: true)

    // Кандидаты в ключи REALITY и WireGuard — base64 длиной от 32 знаков; решает looksLikeKey.
    nonisolated(unsafe) private static let longKey = regex(#"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/_=-])"#, ignoreCase: false)

    /// Строка без секретов. Порядок правил важен: сначала ссылки целиком.
    public static func mask(_ text: String?) -> String {
        guard let text, !text.isEmpty else {
            return ""
        }

        var result = replace(proxyLink, text) { match in match[1] + "://" + hidden }
        result = replace(httpUrl, result) { match in match[2].count > 1 ? match[1] + "/" + hidden : match[0] }
        result = replace(uuid, result) { _ in hidden }
        result = replace(longKey, result) { match in looksLikeKey(match[0]) ? hidden : match[0] }
        return result
    }

    // Случайный ключ почти всегда содержит цифру, строчную и заглавную букву; путь к файлу начинается с «/» или состоит из
    // слов — его не трогаем.
    private static func looksLikeKey(_ value: String) -> Bool {
        let scalars = value.unicodeScalars
        return scalars.first != "/"
            && scalars.filter { $0 == "/" }.count <= 4
            && scalars.contains { ("0"..."9").contains($0) }
            && scalars.contains { ("A"..."Z").contains($0) }
            && scalars.contains { ("a"..."z").contains($0) }
    }

    private static func regex(_ pattern: String, ignoreCase: Bool) -> NSRegularExpression {
        // Выражения постоянные — ошибка в них видна первым же тестом.
        try! NSRegularExpression(pattern: pattern, options: ignoreCase ? [.caseInsensitive] : [])
    }

    /// Замена каждого совпадения: `transform` получает текст группы по номеру (0 — все совпадение, нет группы — пусто).
    private static func replace(_ regex: NSRegularExpression, _ text: String, _ transform: ([String]) -> String) -> String {
        let source = text as NSString
        var result = ""
        var last = 0
        for match in regex.matches(in: text, range: NSRange(location: 0, length: source.length)) {
            let groups = (0..<match.numberOfRanges).map { index in
                let range = match.range(at: index)
                return range.location == NSNotFound ? "" : source.substring(with: range)
            }
            result += source.substring(with: NSRange(location: last, length: match.range.location - last))
            result += transform(groups)
            last = match.range.location + match.range.length
        }

        return result + source.substring(from: last)
    }
}
