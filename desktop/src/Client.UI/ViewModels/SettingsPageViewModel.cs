using Avalonia.Threading;
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
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;
    private readonly IGatewayClient _gateway;
    private readonly OverlayViewModel _overlay;

    public SettingsPageViewModel(SettingsService settings, INavigator navigator, IGatewayClient gateway, OverlayViewModel overlay,
        IExternalProxyStore externalProxy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _navigator = navigator;
        _gateway = gateway;
        _overlay = overlay;
        ExternalProxy = new ExternalProxyEditorViewModel(settings, externalProxy, navigator);
        settings.Changed += OnSettingsChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
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

    /// <summary>Режим VPN — без вопроса: он только усиливает защиту.</summary>
    public bool IsVpnMode
    {
        get => S.Mode == ConnectionMode.Vpn;
        set
        {
            // Снятие отметки VPN — часть выбора «Только прокси»: тот спрашивает сам (ChooseProxyCommand).
            if (value)
            {
                Set(s => s with { Mode = ConnectionMode.Vpn });
            }
        }
    }

    /// <summary>
    /// «Только прокси» — после подтверждения (просьба пользователя 2026-11-01, решение 529): переход с VPN ослабляет защиту,
    /// а случайное нажатие при включенной защите через секунду переподключало бы в режиме прокси. Сегмент сам не
    /// отмечается (<see cref="Controls.ConfirmRadioButton"/>): до ответа режим остается VPN.
    /// </summary>
    public bool IsProxyMode => S.Mode == ConnectionMode.Proxy;

    /// <summary>Отдельная конфигурация внешнего прокси: не меняет выбранный VPN-сервер и локальный порт ядра.</summary>
    public ExternalProxyEditorViewModel ExternalProxy { get; }

    [RelayCommand]
    private async Task ChooseProxyAsync()
    {
        if (S.Mode != ConnectionMode.Proxy && await SafetyDialogs.ConfirmProxyModeAsync(_overlay).ConfigureAwait(true))
        {
            Set(s => s with { Mode = ConnectionMode.Proxy });
        }
    }

    public string ModeNote => IsProxyMode
        ? Loc.Instance["externalProxy.mode.note"]
        : Loc.Instance.Format("externalProxy.vpn.note", OsName);

    // ── Переключатели ────────────────────────────────────────────────────────

    /// <summary>В режиме прокси блок при обрыве недоступен (макет).</summary>
    public bool CanKillSwitch => IsVpnMode;

    /// <summary>Выключение — только после «Выключить» в диалоге: до ответа переключатель стоит на месте.</summary>
    public bool KillSwitch
    {
        get => S.KillSwitch;
        set
        {
            if (value || !S.KillSwitch)
            {
                Set(s => s with { KillSwitch = value });
                return;
            }

            _ = TurnKillSwitchOffAsync();
        }
    }

    /// <summary>Нажатие на переключатель (<see cref="Controls.ConfirmSwitch"/>): выключение — через вопрос.</summary>
    [RelayCommand]
    private void FlipKillSwitch() => KillSwitch = !KillSwitch;

    private async Task TurnKillSwitchOffAsync()
    {
        if (await SafetyDialogs.ConfirmKillSwitchOffAsync(_overlay).ConfigureAwait(true))
        {
            Set(s => s with { KillSwitch = false });
        }
    }

    public string AutostartTitle => Loc.Instance.Format("settings.autostart.title", OsName);

    public string AutostartNote => Loc.Instance.Format("settings.autostart.text", OsName);

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

    public string Footer => Loc.Instance.Format("settings.footer", AppInfo.Version, ServiceTexts.State(_gateway.Service),
        _gateway.Service is ServiceKind.Installed or ServiceKind.User ? "Xray-core" : Loc.Instance["settings.core.none"]);

    [RelayCommand]
    private void Open(string page) => _navigator.Navigate(page == "check" ? Pages.Check : page);

    private static string OsName => AppInfo.OsName;

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

    private void OnSettingsChanged(object? sender, AppSettings settings) => Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        ExternalProxy.Dispose();
        GC.SuppressFinalize(this);
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
