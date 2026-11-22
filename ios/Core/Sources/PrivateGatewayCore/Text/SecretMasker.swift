import Foundation

/// Скрывает секреты в строке перед записью в журнал — как `SecretMasker` десктопа и Android, те же правила: ссылки на
/// серверы, пути и параметры адресов подписок, UUID, ключи. Журнал пользователь может отправить в отчете, поэтому в нем не
/// должно быть ничего, по чему можно подключиться к его серверу.
public enum SecretMasker {
    /// Чем заменяется скрытая часть.
    public static let hidden = "[скрыто]"

    /// Известные учетные данные с формами из ошибок URL и JSON; длинные формы скрываются раньше частей.
    public static func mask(_ text: String?, knownSecrets: [String]) -> String {
        var values = Set<String>()
        func add(_ value: String) {
            guard !value.isEmpty else { return }
            values.insert(value)
            let json = String(JsonText.write(.string(value)).dropFirst().dropLast())
            values.insert(json)
            values.insert(json.replacingOccurrences(of: "/", with: "\\/"))
            let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~")
            if let uri = value.addingPercentEncoding(withAllowedCharacters: allowed) {
                values.insert(uri)
                values.insert(uri.replacingOccurrences(of: "%20", with: "+"))
                values.insert(replace(regex("%[A-Fa-f0-9]{2}", ignoreCase: false), uri) { $0[0].lowercased() })
            }
        }
        for secret in knownSecrets {
            add(secret)
            let trimmed = secret.trimmingCharacters(in: .whitespacesAndNewlines)
            add(trimmed)
            if trimmed.lowercased().hasPrefix("bearer ") || trimmed.lowercased().hasPrefix("basic "), let space = trimmed.firstIndex(of: " ") {
                add(String(trimmed[trimmed.index(after: space)...]).trimmingCharacters(in: .whitespacesAndNewlines))
            }
        }
        var result = text ?? ""
        for secret in values.sorted(by: { $0.utf16.count > $1.utf16.count }) {
            if secret.utf16.count >= 4 { result = result.replacingOccurrences(of: secret, with: hidden) }
            else {
                let expression = regex("(?<![\\p{L}\\p{N}_])" + NSRegularExpression.escapedPattern(for: secret) + "(?![\\p{L}\\p{N}_])", ignoreCase: false)
                result = replace(expression, result) { _ in hidden }
            }
        }
        return mask(result)
    }

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
