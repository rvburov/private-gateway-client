using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Настройки» по макету (доска 4): раздел «Основные» сохраняется сразу при каждом изменении,
/// «Для опытных» — ссылки на подробные экраны.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;
    private readonly IGatewayClient _gateway;

    public SettingsPageViewModel(SettingsService settings, INavigator navigator, IGatewayClient gateway)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _navigator = navigator;
        _gateway = gateway;
        settings.Changed += (_, _) => OnPropertyChanged(string.Empty);
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
        Advanced =
        [
            new("protection", "Icon.Adv.Protection", this),
            new("core", "Icon.Adv.Core", this),
            new("routing", "Icon.Adv.Routing", this),
            new("profiles", "Icon.Adv.Profiles", this),
            new("check", "Icon.Adv.Check", this),
            new("backup", "Icon.Adv.Backup", this),
            new("about", "Icon.Adv.About", this),
        ];
    }

    private AppSettings S => _settings.Current;

    // ── Режим ────────────────────────────────────────────────────────────────

    public bool IsVpnMode
    {
        get => S.Mode == ConnectionMode.Vpn;
        set => Set(s => s with { Mode = value ? ConnectionMode.Vpn : ConnectionMode.Proxy });
    }

    public bool IsProxyMode
    {
        get => S.Mode == ConnectionMode.Proxy;
        set => Set(s => s with { Mode = value ? ConnectionMode.Proxy : ConnectionMode.Vpn });
    }

    public string ModeNote => IsProxyMode
        ? Loc.Instance["settings.mode.proxy.note"]
        : Loc.Instance.Format("settings.mode.vpn.note", OsName);

    // ── Переключатели ────────────────────────────────────────────────────────

    /// <summary>В режиме прокси блок при обрыве недоступен (макет).</summary>
    public bool CanKillSwitch => IsVpnMode;

    public bool KillSwitch
    {
        get => S.KillSwitch;
        set => Set(s => s with { KillSwitch = value });
    }

    public string AutostartTitle => Loc.Instance.Format("settings.autostart.title", OsName);

    public bool StartWithSystem
    {
        get => S.StartWithSystem;
        set => Set(s => s with { StartWithSystem = value });
    }

    public bool ConnectOnLaunch
    {
        get => S.ConnectOnLaunch;
        set => Set(s => s with { ConnectOnLaunch = value });
    }

    public bool MinimizeToTray
    {
        get => S.MinimizeToTray;
        set => Set(s => s with { MinimizeToTray = value });
    }

    // ── Уведомления ──────────────────────────────────────────────────────────

    public bool NotifyAll
    {
        get => S.Notifications == NotificationLevel.All;
        set => SetNotifications(value, NotificationLevel.All);
    }

    public bool NotifyImportant
    {
        get => S.Notifications == NotificationLevel.ImportantOnly;
        set => SetNotifications(value, NotificationLevel.ImportantOnly);
    }

    public bool NotifyOff
    {
        get => S.Notifications == NotificationLevel.Off;
        set => SetNotifications(value, NotificationLevel.Off);
    }

    public string NotificationsNote => S.Notifications switch
    {
        NotificationLevel.All => Loc.Instance["settings.notifications.all.note"],
        NotificationLevel.Off => Loc.Instance["settings.notifications.off.note"],
        _ => Loc.Instance["settings.notifications.important.note"],
    };

    // ── Язык ─────────────────────────────────────────────────────────────────

    public bool IsRussian
    {
        get => S.Language == "ru";
        set => SetLanguage(value, "ru");
    }

    public bool IsEnglish
    {
        get => S.Language == "en";
        set => SetLanguage(value, "en");
    }

    // ── Для опытных ──────────────────────────────────────────────────────────

    public IReadOnlyList<AdvancedLinkViewModel> Advanced { get; }

    public string Footer => Loc.Instance.Format("settings.footer", "0.1",
        _gateway.IsSimulated ? Loc.Instance["settings.service.simulated"] : "—",
        _gateway.IsSimulated ? Loc.Instance["settings.core.none"] : "Xray-core");

    [RelayCommand]
    private void Open(string page) => _navigator.Navigate(page == "check" ? Pages.Check : page);

    private static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    private void Set(Func<AppSettings, AppSettings> change)
    {
        _settings.Update(change);
        OnPropertyChanged(string.Empty);
    }

    private void SetNotifications(bool on, NotificationLevel level)
    {
        if (on)
        {
            Set(s => s with { Notifications = level });
        }
    }

    private void SetLanguage(bool on, string language)
    {
        if (!on)
        {
            return;
        }

        Set(s => s with { Language = language });
        Loc.Instance.SetLanguage(language);
    }
}

/// <summary>Строка раздела «Для опытных»: иконка, название, пояснение, переход.</summary>
public sealed class AdvancedLinkViewModel(string id, string icon, SettingsPageViewModel owner)
{
    public string Id { get; } = id;

    public string Icon { get; } = icon;

    public string Title => Loc.Instance[$"settings.adv.{Id}.title"];

    public string Text => Loc.Instance[$"settings.adv.{Id}.text"];

    public IRelayCommand<string> OpenCommand => owner.OpenCommand;
}
