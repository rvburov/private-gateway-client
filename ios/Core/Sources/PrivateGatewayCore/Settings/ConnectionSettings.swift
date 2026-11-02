import Foundation

/// Подробность журнала ядра («Журнал ядра», доска 06) — как `CoreLogLevel` десктопа. Журнал соединений выключен на любом
/// уровне (`log.access` — none). Ядро телефона пишет журнал в вывод процесса расширения туннеля: в файлы и в «Журнал
/// туннеля» он не попадает.
public enum CoreLogLevel: String, CaseIterable, Sendable {
    case warning = "Warning"
    case info = "Info"
    case debug = "Debug"

    /// Значение `log.loglevel` Xray.
    public var xray: String {
        switch self {
        case .warning: return "warning"
        case .info: return "info"
        case .debug: return "debug"
        }
    }
}

/// Отпечаток TLS (uTLS) — как `TlsFingerprint` десктопа: «Из профиля» — как в ссылке или подписке (нет — выбор ядра, у
/// REALITY — chrome).
public enum TlsFingerprint: String, CaseIterable, Sendable {
    case profile = "Profile"
    case chrome = "Chrome"
    case firefox = "Firefox"
    case safari = "Safari"
    case random = "Random"

    /// Значение `fingerprint` Xray — как его пишут в ссылках (fp=chrome); «Из профиля» — nil.
    public var xray: String? {
        self == .profile ? nil : rawValue.lowercased()
    }
}

/// «Подключение и ядро» телефона (доска 06) — то из `ConnectionSettings` десктопа, что выполняют ядро и туннель телефона:
/// журнал ядра, MTU туннеля, определение домена, отпечаток TLS, Mux, фрагментация TLS и таймаут подключения. Переподключение
/// хранится отдельно (`AppSettings.reconnect`). Стека TUN, строгих маршрутов и локального прокси у телефона нет: ядро
/// телефона стек не выбирает, маршруты держит iOS, а входа прокси для программ нет (решение 527). По умолчанию — как в
/// макете: MTU «Авто», определение домена включено, Mux и фрагментация выключены, ответа сервера ждать 10 секунд.
public struct ConnectionSettings: Equatable, Hashable, Sendable {
    /// Свой MTU — от минимального для IPv6 (1280) до обычного Ethernet (1500), как у десктопа: иначе пакеты режутся.
    public static let minMtu = 1280
    public static let maxMtu = 1500
    /// MTU при «Авто» — тот, с которым туннель телефона работал всегда (и туннель Android).
    public static let autoMtu = 1500
    /// Пункты меню MTU по порядку доски: nil — «Авто».
    public static let mtuChoices: [Int?] = [nil, 1500, 1420, 1280]
    /// Пункты меню таймаута по порядку доски, секунды; первый — по умолчанию.
    public static let timeoutChoices = [10, 5, 20, 30]
    public static let defaultTimeout = 10
    /// Годный срок ответа сервера, секунды, — как у десктопа; иначе — по умолчанию.
    public static let timeoutRange = 1...120

    public var coreLog: CoreLogLevel
    /// Свой MTU туннеля; nil — «Авто».
    public var mtu: Int?
    /// Определение домена (sniffing): имя сайта из TLS, HTTP и QUIC — только для правил по доменам.
    public var sniffing: Bool
    public var fingerprint: TlsFingerprint
    /// Мультиплексирование соединений (не с XTLS Vision).
    public var mux: Bool
    /// Фрагментация TLS ClientHello против DPI.
    public var tlsFragment: Bool
    /// Сколько ждать ответа сервера, секунды: срок пробы связи через туннель после подключения и при наблюдении.
    public var connectTimeout: Int

    public init(
        coreLog: CoreLogLevel = .warning, mtu: Int? = nil, sniffing: Bool = true, fingerprint: TlsFingerprint = .profile, mux: Bool = false,
        tlsFragment: Bool = false, connectTimeout: Int = ConnectionSettings.defaultTimeout
    ) {
        self.coreLog = coreLog
        self.mtu = mtu
        self.sniffing = sniffing
        self.fingerprint = fingerprint
        self.mux = mux
        self.tlsFragment = tlsFragment
        self.connectTimeout = connectTimeout
    }

    /// Годится ли число для своего MTU.
    public static func validMtu(_ value: Int) -> Bool {
        (minMtu...maxMtu).contains(value)
    }

    /// MTU туннеля и входа TUN ядра: свой — если годится, иначе «Авто».
    public var tunMtu: Int {
        if let mtu, Self.validMtu(mtu) {
            return mtu
        }

        return Self.autoMtu
    }

    /// Срок ответа пробы связи, мс: таймаут подключения, а неверный — по умолчанию.
    public var probeTimeout: Int {
        (Self.timeoutRange.contains(connectTimeout) ? connectTimeout : Self.defaultTimeout) * 1000
    }

    /// Число из поля «Свой MTU»: только цифры (пробелы по краям не в счет), от 1280 до 1500; иначе nil.
    public static func parseMtu(_ text: String) -> Int? {
        let digits = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...4).contains(digits.count), digits.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Int(digits), validMtu(value) else {
            return nil
        }

        return value
    }
}
