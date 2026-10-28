using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Состояние строки шага «Установка службы»: ждет, ставится, готово.</summary>
public enum SetupRowState
{
    Pending,
    Busy,
    Done,
}

/// <summary>Строка шага «Установка службы»: что ставится и что с ним.</summary>
public sealed record SetupRow(string Title, string Detail, SetupRowState State)
{
    public bool IsBusy => State == SetupRowState.Busy;

    public bool IsDone => State == SetupRowState.Done;
}

/// <summary>
/// Первый запуск по макету (доска 10): приветствие с выбором языка; «Установка службы» (шаг 1 из 2),
/// если ее можно поставить и она еще не стоит; потом — «Добавьте первый сервер», если своих серверов
/// еще нет. Без службы — только режим прокси: так и выбирается режим.
/// </summary>
public sealed partial class WelcomePageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IServerStore _servers;
    private readonly IGatewayClient _gateway;
    private readonly INavigator _navigator;
    private readonly IServiceInstaller _installer;

    public WelcomePageViewModel(SettingsService settings, IServerStore servers, IGatewayClient gateway, INavigator navigator, IServiceInstaller installer)
    {
        _settings = settings;
        _servers = servers;
        _gateway = gateway;
        _navigator = navigator;
        _installer = installer;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGreeting))]
    public partial bool IsAddServer { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGreeting))]
    public partial bool IsService { get; private set; }

    public bool IsGreeting => !IsAddServer && !IsService;

    /// <summary>Идет установка службы — кнопка «Устанавливаем…» и строка службы со значком ожидания.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows), nameof(InstallText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(WithoutServiceCommand))]
    public partial bool IsInstalling { get; private set; }

    /// <summary>Служба поставлена на этом шаге: кнопка «Продолжить», отметка на следующем шаге.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rows), nameof(InstallText), nameof(CanSkipService))]
    public partial bool IsInstalled { get; private set; }

    /// <summary>Права не дали или установка не вышла — строка под кнопкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInstallError))]
    public partial string? InstallError { get; private set; }

    public bool HasInstallError => InstallError is not null;

    public bool IsRussian
    {
        get => _settings.Current.Language == "ru";
        set => SetLanguage(value, "ru");
    }

    public bool IsEnglish
    {
        get => _settings.Current.Language == "en";
        set => SetLanguage(value, "en");
    }

    /// <summary>«Весь трафик Windows через VPN» — с именем текущей ОС.</summary>
    public string AllTrafficTitle => Loc.Instance.Format("welcome.all.title", AppInfo.OsName);

    /// <summary>«…Windows один раз попросит права администратора» — с именем текущей ОС.</summary>
    public string ServiceText => Loc.Instance.Format("welcome.service.text", AppInfo.OsName);

    /// <summary>
    /// Служба, сетевой адаптер и правила брандмауэра. Ставится служба; адаптер и правила появляются при
    /// первом подключении — так и сказано, а не «готово».
    /// </summary>
    public IReadOnlyList<SetupRow> Rows =>
    [
        new(Loc.Instance["welcome.service.row.service"], IsInstalled ? Loc.Instance["welcome.service.done"]
                : IsInstalling ? Loc.Instance["welcome.service.installing"] : Loc.Instance["welcome.service.row.service.text"],
            IsInstalled ? SetupRowState.Done : IsInstalling ? SetupRowState.Busy : SetupRowState.Pending),
        new(Loc.Instance["welcome.service.row.tun"], IsInstalled ? Loc.Instance["welcome.service.onConnect"] : Loc.Instance["welcome.service.row.tun.text"],
            SetupRowState.Pending),
        new(Loc.Instance["welcome.service.row.firewall"], IsInstalled ? Loc.Instance["welcome.service.onConnect"] : Loc.Instance["welcome.service.row.firewall.text"],
            SetupRowState.Pending),
    ];

    /// <summary>«Установить» → «Устанавливаем…» → «Продолжить» (макет).</summary>
    public string InstallText => Loc.Instance[IsInstalled ? "welcome.service.continue" : IsInstalling ? "welcome.service.busy" : "welcome.service.install"];

    /// <summary>«Без службы — только режим прокси»: пока служба не поставлена.</summary>
    public bool CanSkipService => !IsInstalled;

    /// <summary>В демо-режиме серверы можно посмотреть на демо-данных.</summary>
    public bool CanTryDemo => _gateway.Service == ServiceKind.Simulated;

    /// <summary>
    /// «Начать»: первый запуск пройден. Службу можно поставить и ее нет — шаг «Установка службы»; иначе
    /// своих серверов нет — предложить добавить первый, есть (например, после восстановления копии) —
    /// сразу на главную.
    /// </summary>
    [RelayCommand]
    private void Start()
    {
        _settings.Update(s => s with { OnboardingDone = true });
        if (_installer.IsAvailable && _gateway.Service is not (ServiceKind.Installed or ServiceKind.Simulated))
        {
            IsService = true;
            return;
        }

        Next();
    }

    /// <summary>«Установить»: ОС спрашивает права, служба ставится; после — «Продолжить».</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task InstallAsync()
    {
        if (IsInstalled)
        {
            Next();
            return;
        }

        InstallError = null;
        IsInstalling = true;
        try
        {
            switch (await _installer.InstallAsync().ConfigureAwait(true))
            {
                case ServiceInstallResult.Installed:
                    IsInstalled = true;
                    break;
                case ServiceInstallResult.Cancelled:
                    InstallError = Loc.Instance["problem.install.cancelled"];
                    break;
                default:
                    InstallError = InstallFailure(_installer);
                    break;
            }
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>
    /// Почему не вышла установка: словами самой установки (окно ОС с правами свой вывод не показывает — она пишет причину
    /// в файл итога), не назвала — общая строка.
    /// </summary>
    public static string InstallFailure(IServiceInstaller installer) => installer.LastError is { } reason
        ? Loc.Instance.Format("problem.install.reason", reason)
        : Loc.Instance["problem.install.failed"];

    /// <summary>«Без службы — только режим прокси»: режим запоминается, дальше — как без шага.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private void WithoutService()
    {
        _settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        Next();
    }

    [RelayCommand]
    private void AddServer() => _navigator.Navigate(Pages.Add);

    [RelayCommand]
    private void TryDemo() => _navigator.Navigate(Pages.Home);

    private bool CanWork() => !IsInstalling;

    private void Next()
    {
        IsService = false;
        if (_servers.Servers.Count > 0)
        {
            _navigator.Navigate(Pages.Home);
            return;
        }

        IsAddServer = true;
    }

    private void SetLanguage(bool on, string language)
    {
        if (!on)
        {
            return;
        }

        _settings.Update(s => s with { Language = language });
        Loc.Instance.SetLanguage(language);
    }
}
