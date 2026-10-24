namespace PrivateGateway.Client.Core.Settings;

/// <summary>Какие уведомления показывать (макет, доска 22).</summary>
public enum NotificationLevel
{
    /// <summary>Важные и обычные.</summary>
    All,

    /// <summary>Обрыв, блокировка интернета, вход в Wi-Fi, сбой службы.</summary>
    ImportantOnly,

    /// <summary>Никаких: только значок в трее меняет цвет.</summary>
    Off,
}

/// <summary>Как часто обновлять подписки сами (макет, доска 12).</summary>
public enum SubscriptionRefresh
{
    /// <summary>Только кнопкой «Обновить».</summary>
    Off,
    Every6Hours,
    Every12Hours,
    Every24Hours,
}

/// <summary>
/// Настройки разделов «Основные» (доска 4) и «Профили и подписки» (доска 12). Значения по умолчанию —
/// решения из docs/roadmap/DECISIONS.md и макета: блок при обрыве включен, уведомления — только
/// важные, подписки — каждые 12 часов и только через VPN.
/// </summary>
public sealed record AppSettings(
    ConnectionMode Mode,
    bool KillSwitch,
    bool StartWithSystem,
    bool ConnectOnLaunch,
    bool MinimizeToTray,
    NotificationLevel Notifications,
    string Language,
    SubscriptionRefresh SubscriptionRefresh = SubscriptionRefresh.Every12Hours,
    bool UpdateOnlyViaVpn = true,
    bool NotifySelectedServerChange = true)
{
    public static AppSettings Default { get; } = new(
        ConnectionMode.Vpn,
        KillSwitch: true,
        StartWithSystem: true,
        ConnectOnLaunch: false,
        MinimizeToTray: true,
        NotificationLevel.ImportantOnly,
        Language: "ru");

    /// <summary>Промежуток автообновления подписок; null — выключено.</summary>
    public TimeSpan? RefreshInterval => SubscriptionRefresh switch
    {
        SubscriptionRefresh.Every6Hours => TimeSpan.FromHours(6),
        SubscriptionRefresh.Every12Hours => TimeSpan.FromHours(12),
        SubscriptionRefresh.Every24Hours => TimeSpan.FromHours(24),
        _ => null,
    };
}
