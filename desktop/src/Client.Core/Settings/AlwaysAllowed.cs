namespace PrivateGateway.Client.Core.Settings;

/// <summary>
/// «Пропускается всегда» при «Остальные сайты: Блокировать» (решение 740): обновления системы и защиты, точное время, проверка
/// сети, уведомления и проверка сертификатов — без них устройство ломается. Домен — с поддоменами. Тот же список —
/// shared/specifications/routing/always-allowed.json, тест сверяет. Сайт Private Gateway добавляет конфиг ядра сам.
/// </summary>
public static class AlwaysAllowed
{
    public static IReadOnlyList<string> Updates { get; } =
    [
        "windowsupdate.com", "update.microsoft.com", "delivery.mp.microsoft.com", "dsp.mp.microsoft.com", "wdcp.microsoft.com",
        "wdcpalt.microsoft.com", "definitionupdates.microsoft.com", "swscan.apple.com", "swcdn.apple.com", "swdist.apple.com",
        "mesu.apple.com", "updates.cdn-apple.com", "play.googleapis.com", "android.clients.google.com",
    ];

    public static IReadOnlyList<string> Time { get; } = ["time.windows.com", "time.apple.com", "time.google.com", "time.android.com", "pool.ntp.org"];

    public static IReadOnlyList<string> Network { get; } =
    [
        "msftconnecttest.com", "msftncsi.com", "captive.apple.com", "connectivitycheck.gstatic.com", "connectivitycheck.android.com",
        "clients3.google.com", "nmcheck.gnome.org",
    ];

    public static IReadOnlyList<string> Notifications { get; } =
    [
        "mtalk.google.com", "alt1-mtalk.google.com", "alt2-mtalk.google.com", "alt3-mtalk.google.com", "alt4-mtalk.google.com",
        "alt5-mtalk.google.com", "alt6-mtalk.google.com", "alt7-mtalk.google.com", "alt8-mtalk.google.com", "fcm.googleapis.com",
        "push.apple.com",
    ];

    public static IReadOnlyList<string> Certificates { get; } =
        ["digicert.com", "pki.goog", "lencr.org", "sectigo.com", "usertrust.com", "globalsign.com", "entrust.net"];

    /// <summary>Все домены списка по порядку групп.</summary>
    public static IReadOnlyList<string> Domains { get; } = [.. Updates, .. Time, .. Network, .. Notifications, .. Certificates];

    /// <summary>UDP-порты, которые пропускаются по номеру: время (NTP) имени в пакете не несет.</summary>
    public static IReadOnlyList<int> UdpPorts { get; } = [123];
}
