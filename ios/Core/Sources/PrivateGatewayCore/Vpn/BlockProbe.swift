/// iPhone, «Ошибка» при включенном блоке при обрыве (DECISIONS.md 2026-11-03, п. 10): закрыт ли трафик на самом деле, решает
/// короткая проба сети из приложения (соединение TCP с адресами `targets`), а не догадка по состоянию туннеля в системе. Туннель
/// без ядра пробу не пропустит — «Блокировка» с «Повторить» и вопросом «Снять защиту?» на щите; iOS сняла туннель и трафик
/// идет напрямую — проба проходит: «Ошибка» на главной и уведомление «трафик идет напрямую» теми же словами. Так уведомление
/// и главная не противоречат друг другу.
public enum BlockProbe {
    /// Куда пробовать — адреса без DNS двух разных компаний: сеть открыта, если ответил хоть один. Один закрытый адрес
    /// (оператор режет Cloudflare) не превратит прямой трафик в ложную «Блокировку».
    public static let targets: [(host: String, port: Int)] = [(TunnelProbe.host, TunnelProbe.port), ("8.8.8.8", 443)]

    /// Пробовать: «Ошибка» при блоке при обрыве — только тогда «Блокировка» что-то обещает. Другой VPN (PGC-NET-010) — не
    /// наш туннель и не наш блок: у него своя страница проблемы.
    public static func needed(_ status: TunnelStatus, killSwitch: Bool) -> Bool {
        status.state == .error && killSwitch && status.error != TunnelErrors.otherVpn
    }

    /// «Блокировка» на главной: проба уже ответила — по ней (сеть не ответила — закрыто); пробы еще нет — по туннелю
    /// (поднят, но без ядра — трафик закрыт). Не «Ошибка» — не «Блокировка».
    public static func blocked(_ status: TunnelStatus, answered: Bool?, tunnelUp: Bool) -> Bool {
        guard status.state == .error else {
            return false
        }

        return answered.map { !$0 } ?? tunnelUp
    }

    /// Уведомление, когда блок не держит: «Сервер не отвечает» или «Защита не включилась» со словами «трафик идет напрямую» —
    /// как без блока при обрыве. Сеть закрыта — nil: расширение туннеля уже сказало «Интернет заблокирован».
    public static func notice(_ status: TunnelStatus, answered: Bool, server: String?) -> Notice? {
        guard answered, status.state == .error, status.error != TunnelErrors.otherVpn else {
            return nil
        }

        return NotificationPolicy.forChange(TunnelStatus(), status, server: server, killSwitch: false)
    }

    /// Строка журнала «Проверки» с итогом пробы: адресов нет.
    public static func logLine(answered: Bool) -> String {
        answered ? "network answers without the tunnel  traffic goes direct" : "network closed without the tunnel  traffic blocked"
    }
}
