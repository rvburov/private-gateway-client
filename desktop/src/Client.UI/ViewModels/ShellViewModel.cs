using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Окно: текущая страница, островок навигации с кнопкой-щитом и всплывающая подсказка. Новая ошибка
/// «Служба не запущена» или «Нужны права администратора» открывает экран ошибки (доска 16). О пропавшем или
/// изменившемся выбранном сервере говорит центр уведомлений: всплывашкой в окне или уведомлением ОС.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, INavigator
{
    private readonly Dictionary<string, Func<object>> _pages;
    private readonly Dictionary<string, object> _created = [];

    public ShellViewModel(
        ProtectionViewModel protection,
        ToastViewModel toast,
        OverlayViewModel overlay,
        Func<INavigator, MainPageViewModel> mainPage,
        Func<INavigator, ServersPageViewModel> serversPage,
        Func<INavigator, CheckPageViewModel> checkPage,
        Func<INavigator, NetworkPageViewModel> networkPage,
        Func<INavigator, ProbePageViewModel> probePage,
        Func<INavigator, ImportPageViewModel> importPage,
        Func<INavigator, SettingsPageViewModel> settingsPage,
        Func<INavigator, SubscriptionsPageViewModel> subscriptionsPage,
        Func<INavigator, ProtectionPageViewModel> protectionPage,
        Func<INavigator, CorePageViewModel> corePage,
        Func<INavigator, RoutingPageViewModel> routingPage,
        Func<INavigator, BackupPageViewModel> backupPage,
        Func<INavigator, AboutPageViewModel> aboutPage,
        Func<INavigator, WelcomePageViewModel> welcomePage,
        Func<INavigator, ProblemPageViewModel> problemPage,
        IGatewayClient gateway,
        SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(mainPage);
        ArgumentNullException.ThrowIfNull(serversPage);
        ArgumentNullException.ThrowIfNull(settingsPage);
        Protection = protection;
        Toast = toast;
        Overlay = overlay;
        _pages = new()
        {
            [Pages.Home] = () => mainPage(this),
            [Pages.Servers] = () => serversPage(this),
            [Pages.Check] = () => checkPage(this),
            [Pages.Network] = () => networkPage(this),
            [Pages.Probe] = () => probePage(this),
            [Pages.Settings] = () => settingsPage(this),
            [Pages.Add] = () => importPage(this),
            [Pages.Protection] = () => protectionPage(this),
            [Pages.Core] = () => corePage(this),
            [Pages.Routing] = () => routingPage(this),
            [Pages.Profiles] = () => subscriptionsPage(this),
            [Pages.Backup] = () => backupPage(this),
            [Pages.About] = () => aboutPage(this),
            [Pages.Welcome] = () => welcomePage(this),
            [Pages.Problem] = () => problemPage(this),
        };

        // Первый запуск — приветствие (доска 10), дальше — сразу главная.
        Navigate(settings.Current.OnboardingDone ? Pages.Home : Pages.Welcome);

        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => ShowProblem(status));
    }

    public ProtectionViewModel Protection { get; }

    public ToastViewModel Toast { get; }

    /// <summary>Нижний лист и диалоги поверх окна вместе с островком.</summary>
    public OverlayViewModel Overlay { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsServers), nameof(IsCheck), nameof(IsSettings), nameof(IsIslandVisible))]
    public partial string CurrentKey { get; private set; } = Pages.Home;

    /// <summary>Островок скрыт на экранах-шагах: «Добавить профиль» (доска 3) и первый запуск (доска 10).</summary>
    public bool IsIslandVisible => CurrentKey is not (Pages.Add or Pages.Welcome);

    private string _previousKey = Pages.Home;

    [ObservableProperty]
    public partial object? CurrentPage { get; private set; }

    public bool IsHome => CurrentKey == Pages.Home;

    public bool IsServers => CurrentKey == Pages.Servers;

    public bool IsCheck => CurrentKey is Pages.Check or Pages.Network or Pages.Probe;

    /// <summary>Подстраницы настроек подсвечивают в островке «Настройки».</summary>
    public bool IsSettings => CurrentKey is Pages.Settings or Pages.Protection or Pages.Core or Pages.Routing or Pages.Profiles or Pages.Backup or Pages.About;

    [RelayCommand]
    public void Navigate(string page)
    {
        if (!_pages.TryGetValue(page, out var factory))
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Нет такой страницы");
        }

        if (!_created.TryGetValue(page, out var viewModel))
        {
            viewModel = factory();
            _created[page] = viewModel;
        }

        Overlay.CloseSheet();
        if (page != CurrentKey)
        {
            _previousKey = CurrentKey;
        }

        CurrentKey = page;
        CurrentPage = viewModel;
    }

    public void Back() => Navigate(_previousKey == CurrentKey ? Pages.Home : _previousKey);

    // Последняя показанная ошибка службы: то же состояние, присланное повторно, экран не открывает.
    private DateTimeOffset? _problemSince;

    /// <summary>
    /// Новая ошибка службы — экран ошибки, на каком бы экране ни был. «Работает другой VPN» — и в «Блокировке»: в
    /// строгом режиме и при загрузке отказ сеть не открывает.
    /// </summary>
    private void ShowProblem(ConnectionStatus status)
    {
        if (status is not ({ State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning or ErrorCodes.AdminRequired or ErrorCodes.OtherVpn }
                or { State: ConnectionState.Blocked, ErrorCode: ErrorCodes.OtherVpn })
            || status.Since == _problemSince)
        {
            return;
        }

        _problemSince = status.Since;
        Navigate(Pages.Problem);
    }
}
