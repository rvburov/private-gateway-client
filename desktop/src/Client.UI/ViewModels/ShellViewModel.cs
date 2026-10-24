using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Окно: текущая страница, островок навигации с кнопкой-щитом и всплывающая подсказка.
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
        Func<CheckPageViewModel> checkPage,
        Func<INavigator, ImportPageViewModel> importPage,
        Func<INavigator, SettingsPageViewModel> settingsPage)
    {
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
            [Pages.Check] = checkPage,
            [Pages.Settings] = () => settingsPage(this),
            [Pages.Add] = () => importPage(this),
            ["protection"] = () => new StubPageViewModel("settings.adv.protection.title"),
            ["core"] = () => new StubPageViewModel("settings.adv.core.title"),
            ["routing"] = () => new StubPageViewModel("settings.adv.routing.title"),
            ["profiles"] = () => new StubPageViewModel("settings.adv.profiles.title"),
            ["backup"] = () => new StubPageViewModel("settings.adv.backup.title"),
            ["about"] = () => new StubPageViewModel("settings.adv.about.title"),
        };
        Navigate(Pages.Home);
    }

    public ProtectionViewModel Protection { get; }

    public ToastViewModel Toast { get; }

    /// <summary>Нижний лист и диалоги поверх окна вместе с островком.</summary>
    public OverlayViewModel Overlay { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsServers), nameof(IsCheck), nameof(IsSettings), nameof(IsIslandVisible))]
    public partial string CurrentKey { get; private set; } = Pages.Home;

    /// <summary>Островок скрыт на экранах-шагах с кнопкой «назад» («Добавить профиль», доска 3).</summary>
    public bool IsIslandVisible => CurrentKey != Pages.Add;

    private string _previousKey = Pages.Home;

    [ObservableProperty]
    public partial object? CurrentPage { get; private set; }

    public bool IsHome => CurrentKey == Pages.Home;

    public bool IsServers => CurrentKey == Pages.Servers;

    public bool IsCheck => CurrentKey == Pages.Check;

    /// <summary>Подстраницы настроек подсвечивают в островке «Настройки».</summary>
    public bool IsSettings => CurrentKey is Pages.Settings or "protection" or "core" or "routing" or "profiles" or "backup" or "about";

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
}
