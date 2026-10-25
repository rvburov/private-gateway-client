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

/// <summary>Откуда брать обновления программы (доска 15).</summary>
public enum UpdateChannel
{
    /// <summary>Только выпуски.</summary>
    Stable,

    /// <summary>Предварительные версии — раньше, но с ошибками.</summary>
    Beta,
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

    /// <summary>«Защита и утечки» (доска 5); блок при обрыве — <see cref="KillSwitch"/>.</summary>
    public ProtectionSettings Protection { get; init; } = ProtectionSettings.Default;

    /// <summary>«Подключение и ядро» (доска 6).</summary>
    public ConnectionSettings Connection { get; init; } = ConnectionSettings.Default;

    /// <summary>«Маршрутизация» (доска 13).</summary>
    public RoutingSettings Routing { get; init; } = RoutingSettings.Default;

    /// <summary>Канал обновлений («О программе», доска 15); действует, когда появятся подписанные выпуски.</summary>
    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.Stable;

    /// <summary>Промежуток автообновления подписок; null — выключено.</summary>
    public TimeSpan? RefreshInterval => SubscriptionRefresh switch
    {
        SubscriptionRefresh.Every6Hours => TimeSpan.FromHours(6),
        SubscriptionRefresh.Every12Hours => TimeSpan.FromHours(12),
        SubscriptionRefresh.Every24Hours => TimeSpan.FromHours(24),
        _ => null,
    };
}
