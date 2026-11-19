/// «Пропускается всегда» при «Остальные сайты: Блокировать» (решение 740): обновления системы и защиты, точное время,
/// проверка сети, уведомления и проверка сертификатов — без них телефон ломается. Домен — с поддоменами. Тот же список —
/// shared/specifications/routing/always-allowed.json, тест сверяет. Сайт Private Gateway — `site`.
public enum AlwaysAllowed {
    public static let updates = [
        "windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com", "dsp.mp.microsoft.com", "wdcp.microsoft.com",
        "wdcpalt.microsoft.com", "definitionupdates.microsoft.com", "swscan.apple.com", "swcdn.apple.com", "swdist.apple.com",
        "mesu.apple.com", "updates.cdn-apple.com", "play.googleapis.com", "android.clients.google.com",
    ]

    public static let time = ["time.windows.com", "time.apple.com", "time.google.com", "time.android.com", "pool.ntp.org"]

    public static let network = [
        "msftconnecttest.com", "msftncsi.com", "captive.apple.com", "connectivitycheck.gstatic.com", "connectivitycheck.android.com",
        "clients3.google.com", "nmcheck.gnome.org",
    ]

    public static let notifications = [
        "mtalk.google.com", "alt1-mtalk.google.com", "alt2-mtalk.google.com", "alt3-mtalk.google.com", "alt4-mtalk.google.com",
        "alt5-mtalk.google.com", "alt6-mtalk.google.com", "alt7-mtalk.google.com", "alt8-mtalk.google.com", "fcm.googleapis.com",
        "push.apple.com",
    ]

    public static let certificates = ["digicert.com", "pki.goog", "lencr.org", "sectigo.com", "usertrust.com", "globalsign.com", "entrust.net"]

    /// Сайт Private Gateway Web (вход и обновления, как AccountSite десктопа и AccountApi.SITE Android).
    public static let site = "private-gateway.duckdns.org"

    /// Все домены списка по порядку групп.
    public static let domains = updates + time + network + notifications + certificates

    /// UDP-порты, которые пропускаются по номеру: время (NTP) имени в пакете не несет.
    public static let udpPorts = [123]
}
