/// Форма числа для ключей с окончаниями .one, .few и .many («import.add.few» — «Добавить 3 сервера»), как `Plural` Android
/// и `Loc.Plural` десктопа: русский — по последним цифрам (1, 21 — one; 2–4, 22–24 — few; 11–14 и остальные — many),
/// английский — one только для 1.
public enum Plural {
    /// - Parameter language: код языка текстов на экране («ru», «en»).
    public static func form(_ count: Int, _ language: String) -> String {
        if language != "ru" {
            return count == 1 ? "one" : "many"
        }

        let tens = count % 100
        if count % 10 == 1, tens != 11 {
            return "one"
        }

        if (2...4).contains(count % 10), tens < 12 || tens > 14 {
            return "few"
        }

        return "many"
    }
}

/// Ключи текстов по кодам — как `Texts` Android: коды ошибок, причины отказа загрузки, форматы импорта и формы чисел. Ключи
/// общие для всех приложений (shared/specifications/localization); текст по ключу дает `PgTexts.text(key:)`.
public enum TextKeys {
    /// error.<код> — для кодов ошибок туннеля; неизвестный код — «Ядро не запустилось».
    public static func error(_ code: String?) -> String {
        let known = [TunnelErrors.coreFailed, TunnelErrors.noServer, TunnelErrors.unsupported, TunnelErrors.serverNotResponding, TunnelErrors.otherVpn]
        return "error." + (code.flatMap { known.contains($0) ? $0 : nil } ?? TunnelErrors.coreFailed)
    }

    /// import.error.<код> — почему строка импорта пропущена; неизвестный — «не похоже на ссылку».
    public static func importError(_ code: String?) -> String {
        let known = [
            ImportErrors.unknownScheme, ImportErrors.badHost, ImportErrors.badPort, ImportErrors.noCredentials, ImportErrors.badEncoding,
            ImportErrors.unsupportedTransport, ImportErrors.unsupportedMethod, ImportErrors.unsupportedParameter,
        ]
        return "import.error." + (code.flatMap { known.contains($0) ? $0 : nil } ?? ImportErrors.badLink)
    }

    /// import.format.<формат>.
    public static func importFormat(_ format: String) -> String {
        "import.format." + (ImportParser.formats.contains(format) ? format : ImportParser.formatLinks)
    }

    /// import.download.<причина> и подстановка (код ответа), если она есть в тексте.
    public static func failure(_ reason: FailureReason) -> (key: String, value: String?) {
        switch reason {
        case .notFound: return ("import.download.not_found", nil)
        case let .forbidden(code): return ("import.download.forbidden", String(code))
        case let .status(code): return ("import.download.status", String(code))
        case .tooLarge: return ("import.download.too_large", nil)
        case .timeout: return ("import.download.timeout", nil)
        case .nameResolution: return ("import.download.dns", nil)
        case .connection: return ("import.download.connection", nil)
        case .tls: return ("import.download.tls", nil)
        case .noServers: return ("import.download.no_servers", nil)
        case .noAddress: return ("import.download.no_address", nil)
        case .network: return ("import.download.network", nil)
        }
    }

    /// «Добавить 3 сервера».
    public static func importAdd(_ count: Int, _ language: String) -> String {
        "import.add." + Plural.form(count, language)
    }

    /// «Добавлено 3 сервера».
    public static func imported(_ count: Int, _ language: String) -> String {
        "import.added." + Plural.form(count, language)
    }

    /// «2 ошибки».
    public static func importErrors(_ count: Int, _ language: String) -> String {
        "import.errors." + Plural.form(count, language)
    }
}
