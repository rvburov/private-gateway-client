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

/// <summary>
/// Настройки раздела «Основные» (макет, доска 4). Значения по умолчанию — решения из
/// docs/roadmap/DECISIONS.md: блок при обрыве включен, уведомления — только важные.
/// </summary>
public sealed record AppSettings(
    ConnectionMode Mode,
    bool KillSwitch,
    bool StartWithSystem,
    bool ConnectOnLaunch,
    bool MinimizeToTray,
    NotificationLevel Notifications,
    string Language)
{
    public static AppSettings Default { get; } = new(
        ConnectionMode.Vpn,
        KillSwitch: true,
        StartWithSystem: true,
        ConnectOnLaunch: false,
        MinimizeToTray: true,
        NotificationLevel.ImportantOnly,
        Language: "ru");
}
