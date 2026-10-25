using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Строка фактов на экране ошибки: что проверено и что вышло; кружок — красный, оранжевый или серый.</summary>
public sealed record ProblemFact(string Label, string Value, bool IsBad = false, bool IsWarning = false);

/// <summary>
/// Ошибки службы по макету (доска 16): «Служба не запущена» (PGC-SVC-001), «Нужны права
/// администратора» (PGC-SVC-004 — работает служба пользователя, а просили VPN) и «Работает другой VPN»
/// (PGC-NET-010 — служба нашла чужой туннель с маршрутом ко всему интернету). Выход из первых двух —
/// установить службу (окно просит у ОС права: «Установить службу», «Запросить еще раз») или режим прокси
/// через службу пользователя: окно запускает ее само. Из третьего — проверить снова, когда чужой VPN
/// выключен, или подключиться вопреки ему. Окно при этом работает: островок виден.
/// </summary>
public sealed partial class ProblemPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly SettingsService _settings;
    private readonly IServerCatalog _catalog;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;
    private readonly IServiceInstaller _installer;
    private readonly IUserProxySettings _userProxy;

    public ProblemPageViewModel(IGatewayClient gateway, SettingsService settings, IServerCatalog catalog, INavigator navigator, ToastViewModel toast,
        IServiceInstaller installer, IUserProxySettings userProxy)
    {
        _userProxy = userProxy;
        _gateway = gateway;
        _installer = installer;
        _settings = settings;
        _catalog = catalog;
        _navigator = navigator;
        _toast = toast;
        Code = gateway.Status.ErrorCode is ErrorCodes.AdminRequired or ErrorCodes.OtherVpn ? gateway.Status.ErrorCode : ErrorCodes.ServiceNotRunning;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() =>
        {
            if (status is { State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning or ErrorCodes.AdminRequired or ErrorCodes.OtherVpn }
                or { State: ConnectionState.Blocked, ErrorCode: ErrorCodes.OtherVpn })
            {
                Code = status.ErrorCode;
            }
        });
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
        _ = ReadProxyAsync();
    }

    /// <summary>Системный прокси другой программы — строка на «Работает другой VPN»; нет его — null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Facts))]
    public partial UserProxy? ForeignProxy { get; private set; }

    /// <summary>Код ошибки, которую показывает экран; меняется вместе с новой ошибкой службы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdmin), nameof(IsConflict), nameof(IsCaution), nameof(IsService), nameof(Title), nameof(Text), nameof(Facts),
        nameof(HasCancel), nameof(DetailsText), nameof(InstallText), nameof(CanInstall), nameof(HasProxy))]
    public partial string Code { get; private set; }

    /// <summary>«Нужны права администратора» — оранжевый замок.</summary>
    public bool IsAdmin => Code == ErrorCodes.AdminRequired;

    /// <summary>«Работает другой VPN» — оранжевый треугольник.</summary>
    public bool IsConflict => Code == ErrorCodes.OtherVpn;

    /// <summary>Знак оранжевый: беда не в службе, а в правах или другой программе.</summary>
    public bool IsCaution => IsAdmin || IsConflict;

    /// <summary>«Служба не запущена» — красный щит.</summary>
    public bool IsService => !IsCaution;

    public string Title => Loc.Instance[IsConflict ? "problem.conflict.title" : IsAdmin ? "problem.admin.title" : "problem.service.title"];

    public string Text => Loc.Instance[IsConflict ? "problem.conflict.text" : IsAdmin ? "problem.admin.text" : "problem.service.text"];

    /// <summary>
    /// Факты по макету. У «Работает другой VPN» — адаптер и маршрут: служба считает чужим VPN туннель,
    /// который держит маршрут ко всему интернету, — оба факта всегда верны; имя адаптера — в журнале.
    /// Между ними — системный прокси, если его поставила другая программа (читает окно: настройки пользователя).
    /// </summary>
    public IReadOnlyList<ProblemFact> Facts => IsConflict
        ?
        [
            new(Loc.Instance["problem.fact.adapter"], Loc.Instance["problem.fact.adapter.active"], IsWarning: true),
            .. ForeignProxy is { } proxy
                ? [new ProblemFact(Loc.Instance["problem.fact.system_proxy"], proxy.IsScript ? Loc.Instance["problem.fact.system_proxy.script"] : proxy.Address,
                    IsWarning: true)]
                : Array.Empty<ProblemFact>(),
            new(Loc.Instance["problem.fact.route"], Loc.Instance["problem.fact.route.taken"], IsWarning: true),
        ]
        : IsAdmin
        ?
        [
            new(Loc.Instance["problem.fact.service"], Loc.Instance["problem.fact.service.user"], IsWarning: true),
            new(Loc.Instance["problem.fact.vpn"], Loc.Instance["problem.fact.vpn.unavailable"], IsWarning: true),
            new(Loc.Instance["problem.fact.proxy"], Loc.Instance["problem.fact.proxy.available"]),
        ]
        :
        [
            new(Loc.Instance["problem.fact.service"], Loc.Instance["problem.fact.service.missing"], IsBad: true),
            new(Loc.Instance["problem.fact.tun"], Loc.Instance["problem.fact.no"], IsBad: true),
            new(Loc.Instance["problem.fact.firewall"], Loc.Instance["problem.fact.no"]),
        ];

    /// <summary>«Отмена» — только у «Нужны права администратора»: там уже есть рабочая служба.</summary>
    public bool HasCancel => IsAdmin;

    /// <summary>
    /// Установить службу можно: Windows или Linux, файл службы рядом с программой. Нельзя — главная кнопка
    /// «режим прокси». Другому VPN установка не поможет.
    /// </summary>
    public bool CanInstall => _installer.IsAvailable && !IsConflict;

    /// <summary>«Установить службу» или, когда уже работает служба пользователя, «Запросить еще раз» (макет).</summary>
    public string InstallText => Loc.Instance[IsAdmin ? "problem.admin.retry" : "problem.install"];

    /// <summary>«Работать в режиме прокси»: на «Нужны права» с установкой его нет — по макету «Запросить еще раз» и «Отмена».</summary>
    public bool HasProxy => !IsConflict && (!IsAdmin || !CanInstall);

    /// <summary>«Подробнее в журнале · код PGC-SVC-001».</summary>
    public string DetailsText => Loc.Instance.Format("problem.details", Code);

    /// <summary>Идет запуск службы пользователя и подключение.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseProxyCommand), nameof(InstallCommand), nameof(RetryCommand), nameof(ContinueCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>
    /// Установить службу: ОС спрашивает права, служба ставится и отвечает — подключение к выбранному
    /// серверу в режиме из настроек. Права не дали или установка не вышла — подсказка, экран остается.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task InstallAsync()
    {
        IsWorking = true;
        ServiceInstallResult result;
        try
        {
            result = await _installer.InstallAsync().ConfigureAwait(true);
            if (result == ServiceInstallResult.Installed && _catalog.Selected is { } server)
            {
                await _gateway.ConnectAsync(server).ConfigureAwait(true);
            }
        }
        finally
        {
            IsWorking = false;
        }

        _toast.Show(Loc.Instance[result switch
        {
            ServiceInstallResult.Installed => "problem.install.done",
            ServiceInstallResult.Cancelled => "problem.install.cancelled",
            _ => "problem.install.failed",
        }], ToastViewModel.LongDuration);
        if (result == ServiceInstallResult.Installed)
        {
            _navigator.Navigate(_catalog.Selected is null ? Pages.Add : Pages.Home);
        }
    }

    /// <summary>
    /// «Работать в режиме прокси»: режим — прокси, подключение к выбранному серверу; службы нет — окно
    /// запускает службу пользователя. Сервера нет — сначала добавить. Не вышло — подсказка, экран остается.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task UseProxyAsync()
    {
        _settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        if (_catalog.Selected is not { } server)
        {
            _navigator.Navigate(Pages.Add);
            return;
        }

        IsWorking = true;
        try
        {
            await _gateway.ConnectAsync(server).ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }

        if (_gateway.Status is { State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning })
        {
            _toast.Show(Loc.Instance["problem.proxy.failed"], ToastViewModel.LongDuration);
            return;
        }

        _navigator.Navigate(Pages.Home);
    }

    /// <summary>
    /// «Я отключил его — проверить снова»: подключение к выбранному серверу заново — служба проверяет
    /// маршруты еще раз. Чужой VPN на месте — подсказка, экран остается; нет — главная.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task RetryAsync() => ConnectAndGoHomeAsync(_gateway.ConnectAsync);

    /// <summary>«Продолжить (не рекомендуется)»: подключиться вопреки чужому VPN — до отключения защиты.</summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private Task ContinueAsync() => ConnectAndGoHomeAsync(_gateway.ConnectDespiteOtherVpnAsync);

    private async Task ConnectAndGoHomeAsync(Func<ServerProfile, CancellationToken, Task> connect)
    {
        if (_catalog.Selected is not { } server)
        {
            _navigator.Navigate(Pages.Add);
            return;
        }

        IsWorking = true;
        try
        {
            await connect(server, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }

        // В строгом режиме и при загрузке отказ оставляет «Блокировку» с тем же кодом.
        if (_gateway.Status is { State: ConnectionState.Error or ConnectionState.Blocked, ErrorCode: ErrorCodes.OtherVpn })
        {
            _toast.Show(Loc.Instance["problem.conflict.still"], ToastViewModel.LongDuration);
            return;
        }

        _navigator.Navigate(Pages.Home);
    }

    private bool CanWork() => !IsWorking;

    /// <summary>
    /// Системный прокси пользователя — в фоне: на Linux и macOS его читает отдельная программа. «Работает другой
    /// VPN» бывает только в режиме VPN, а там системный прокси служба не ставит — он всегда чужой.
    /// </summary>
    private async Task ReadProxyAsync() => ForeignProxy = await Task.Run(_userProxy.Read).ConfigureAwait(true);

    [RelayCommand]
    private void Cancel() => _navigator.Navigate(Pages.Home);

    /// <summary>Журнал — на экране «Проверка»: там записи окна о поиске и запуске службы.</summary>
    [RelayCommand]
    private void Details() => _navigator.Navigate(Pages.Check);
}
