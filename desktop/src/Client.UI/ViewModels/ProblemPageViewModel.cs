using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
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
/// (PGC-NET-010 — служба нашла другую программу VPN или прокси: ее ядро, туннель с маршрутом ко всему интернету или
/// вход системного прокси). Выход из первых двух — установить службу (окно просит у ОС права: «Установить службу»,
/// «Запросить еще раз») или режим прокси через службу пользователя: окно запускает ее само. Из третьего — закрыть ту
/// программу и проверить снова: рядом с ней защита не включается (решение 507), подключаться вопреки ей — только с
/// настройкой «Разрешить работу рядом с другим VPN». Окно при этом работает: островок виден.
/// </summary>
public sealed partial class ProblemPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;
    private readonly IServiceInstaller _installer;
    private readonly IUserProxySettings _userProxy;
    private readonly ConnectionTarget _target;
    private readonly IServiceAccess _access;
    private int _accessRead;

    public ProblemPageViewModel(IGatewayClient gateway, SettingsService settings, INavigator navigator, ToastViewModel toast,
        IServiceInstaller installer, IUserProxySettings userProxy, ConnectionTarget target, IServiceAccess access)
    {
        _access = access;
        _userProxy = userProxy;
        _target = target;
        _gateway = gateway;
        _installer = installer;
        _settings = settings;
        _navigator = navigator;
        _toast = toast;
        Code = gateway.Status.ErrorCode is ErrorCodes.AdminRequired or ErrorCodes.OtherVpn ? gateway.Status.ErrorCode : ErrorCodes.ServiceNotRunning;
        Others = gateway.Status.OtherClients;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() =>
        {
            if (status is { State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning or ErrorCodes.AdminRequired or ErrorCodes.OtherVpn }
                or { State: ConnectionState.Blocked, ErrorCode: ErrorCodes.OtherVpn })
            {
                Code = status.ErrorCode;
                Others = status.OtherClients;
            }
        });
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
        _ = ReadProxyAsync();
    }

    /// <summary>Системный прокси другой программы — строка на «Работает другой VPN»; нет его — null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Facts))]
    public partial UserProxy? ForeignProxy { get; private set; }

    /// <summary>Другие программы VPN и прокси, которые нашла служба (PGC-NET-010); служба старее окна — null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Text), nameof(Facts))]
    public partial OtherClientList? Others { get; private set; }

    /// <summary>Код ошибки, которую показывает экран; меняется вместе с новой ошибкой службы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdmin), nameof(IsConflict), nameof(IsCaution), nameof(IsService), nameof(Title), nameof(Text), nameof(Facts),
        nameof(HasCancel), nameof(DetailsText), nameof(InstallText), nameof(CanInstall), nameof(HasProxy), nameof(NeedsApproval), nameof(CanCheckService), nameof(IsPermission), nameof(ProxyIsPrimary))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial string Code { get; private set; }

    partial void OnCodeChanged(string value) => _ = RefreshServiceAccessAsync();

    /// <summary>«Нужны права администратора» — оранжевый замок.</summary>
    public bool IsAdmin => Code == ErrorCodes.AdminRequired;

    /// <summary>«Работает другой VPN» — оранжевый треугольник.</summary>
    public bool IsConflict => Code == ErrorCodes.OtherVpn;

    /// <summary>Знак оранжевый: беда не в службе, а в правах или другой программе.</summary>
    public bool IsCaution => IsAdmin || IsConflict || NeedsApproval;

    public bool IsPermission => IsAdmin || NeedsApproval;

    /// <summary>«Служба не запущена» — красный щит.</summary>
    public bool IsService => !IsCaution;

    private bool IsServiceError => Code == ErrorCodes.ServiceNotRunning;

    public bool NeedsApproval => IsServiceError && Registration == ServiceAccess.RequiresApproval;

    public bool CanCheckService => IsServiceError && _access.IsSupported && Registration != ServiceAccess.NotInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsApproval), nameof(IsPermission), nameof(IsCaution), nameof(IsService), nameof(Title), nameof(Text), nameof(Facts), nameof(CanInstall), nameof(CanCheckService), nameof(ProxyIsPrimary))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial ServiceAccess Registration { get; private set; } = ServiceAccess.Unknown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(ProxyIsPrimary))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckServiceCommand))]
    public partial bool IsCheckingAccess { get; private set; }

    /// <summary>
    /// «Работает другой VPN или прокси-клиент» — всегда общими словами (просьба пользователя 2026-11-01): какая программа и чем
    /// выдала себя — в фактах ниже.
    /// </summary>
    public string Title => Loc.Instance[NeedsApproval ? "problem.background.title"
        : IsConflict ? "problem.conflict.title" : IsAdmin ? "problem.admin.title"
        : _access.IsSupported ? Registration == ServiceAccess.NotInstalled ? "problem.missing.title" : "problem.unavailable.title"
        : "problem.service.title"];

    public string Text => Loc.Instance[NeedsApproval ? "problem.background.text"
        : IsConflict ? "problem.conflict.text" : IsAdmin ? "problem.admin.text"
        : _access.IsSupported ? Registration switch
        {
            ServiceAccess.Installed => "problem.unavailable.text",
            ServiceAccess.NotInstalled => "problem.missing.text",
            _ => "problem.unknown.text",
        } : "problem.service.text"];

    /// <summary>
    /// Факты по макету. У «Работает другой VPN или прокси-клиент» — что нашла служба, признаками и без названий программ
    /// (решение 525): работает ядро прокси, чужой адаптер с маршрутом ко всему интернету, системный прокси с адресом — строка
    /// на признак. Служба старее окна и находок не присылает — адаптер и маршрут: тогда она считает
    /// чужим VPN только туннель, который держит маршрут ко всему интернету. И системный прокси, если его поставила
    /// другая программа (читает окно: настройки пользователя), — когда служба не назвала его сама.
    /// </summary>
    public IReadOnlyList<ProblemFact> Facts => IsServiceError && _access.IsSupported
        ?
        [
            new(Loc.Instance["problem.fact.service"], Loc.Instance[Registration switch
            {
                ServiceAccess.Installed or ServiceAccess.RequiresApproval => "problem.fact.service.installed",
                ServiceAccess.NotInstalled => "problem.fact.service.missing",
                _ => "problem.fact.service.unknown",
            }]),
            NeedsApproval
                ? new(Loc.Instance["problem.background.fact"], Loc.Instance["problem.background.denied"], IsWarning: true)
                : new(Loc.Instance["problem.connection.fact"], Loc.Instance["problem.connection.unavailable"], IsWarning: true),
        ]
        : IsConflict
        ?
        [
            .. Others is { Items.Count: > 0 } others
                ? others.Items.Select(o => o.Sign switch
                {
                    OtherClientSign.Adapter => new ProblemFact(Loc.Instance["problem.fact.adapter"], Loc.Instance["problem.fact.adapter.active"], IsWarning: true),
                    OtherClientSign.Process => new ProblemFact(Loc.Instance["problem.fact.core"], Loc.Instance["problem.fact.core.running"], IsWarning: true),
                    _ => new ProblemFact(Loc.Instance["problem.fact.system_proxy"], o.Detail, IsWarning: true),
                }).Distinct()
                : [new ProblemFact(Loc.Instance["problem.fact.adapter"], Loc.Instance["problem.fact.adapter.active"], IsWarning: true)],
            .. ForeignProxy is { } proxy && Others?.Items.Any(o => o.Sign == OtherClientSign.SystemProxy) != true
                ? [new ProblemFact(Loc.Instance["problem.fact.system_proxy"], proxy.IsScript ? Loc.Instance["problem.fact.system_proxy.script"] : proxy.Address,
                    IsWarning: true)]
                : Array.Empty<ProblemFact>(),
            .. Others is { Items.Count: > 0 } ? Array.Empty<ProblemFact>()
                : [new ProblemFact(Loc.Instance["problem.fact.route"], Loc.Instance["problem.fact.route.taken"], IsWarning: true)],
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
    /// Установить службу можно, если доступен установщик и на macOS подтверждено отсутствие службы.
    /// Уже установленной службе и другому VPN повторная установка не предлагается.
    /// </summary>
    public bool CanInstall => _installer.IsAvailable && !IsConflict && !IsCheckingAccess
        && (!IsServiceError || !_access.IsSupported || Registration == ServiceAccess.NotInstalled);

    /// <summary>«Установить службу» или, когда уже работает служба пользователя, «Запросить еще раз» (макет).</summary>
    public string InstallText => Loc.Instance[IsAdmin ? "problem.admin.retry" : "problem.install"];

    /// <summary>«Работать в режиме прокси»: на «Нужны права» с установкой его нет — по макету «Запросить еще раз» и «Отмена».</summary>
    public bool ProxyIsPrimary => !CanInstall && !CanCheckService;

    public bool HasProxy => !IsConflict && (!IsAdmin || !CanInstall);

    /// <summary>«Подробнее в журнале · код PGC-SVC-001».</summary>
    public string DetailsText => Loc.Instance.Format("problem.details", Code);

    /// <summary>Идет запуск службы пользователя и подключение.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseProxyCommand), nameof(InstallCommand), nameof(RetryCommand), nameof(CheckServiceCommand), nameof(OpenServiceSettingsCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>
    /// Установить службу: ОС спрашивает права, служба ставится и отвечает — подключение к выбранному
    /// серверу в режиме из настроек. Права не дали или установка не вышла — подсказка, экран остается.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallNow))]
    private async Task InstallAsync()
    {
        await RefreshServiceAccessAsync().ConfigureAwait(true);
        if (!CanInstall)
        {
            return;
        }

        IsInstallingService = true;
        InstallPercent = 0;
        IsWorking = true;
        ServiceInstallResult result;
        try
        {
            result = await _installer.InstallAsync(new Progress<int>(value => InstallPercent = value)).ConfigureAwait(true);
            if (result == ServiceInstallResult.Installed && _target.Selected is { } server)
            {
                await _gateway.ConnectAsync(server).ConfigureAwait(true);
            }
        }
        finally
        {
            IsWorking = false;
            IsInstallingService = false;
        }

        _toast.Show(result switch
        {
            ServiceInstallResult.Installed => Loc.Instance["problem.install.done"],
            ServiceInstallResult.Cancelled => Loc.Instance["problem.install.cancelled"],
            _ => WelcomePageViewModel.InstallFailure(_installer),
        }, ToastViewModel.LongDuration);
        if (result == ServiceInstallResult.Installed)
        {
            _navigator.Navigate(_target.Selected is null ? _target.IsExternalProxy ? Pages.Settings : Pages.Add : Pages.Home);
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
        if (_target.Selected is not { } server)
        {
            _navigator.Navigate(Pages.Settings);
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
    /// «Я закрыл его — проверить снова»: подключение к выбранному серверу заново — служба ищет другие программы еще раз.
    /// Программа на месте — подсказка, экран остается; нет — главная. Кнопки «Продолжить» нет (решение 507): рядом с другим
    /// VPN защита не включается — для особых случаев есть настройка «Разрешить работу рядом с другим VPN».
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RetryAsync()
    {
        if (_target.Selected is not { } server)
        {
            _navigator.Navigate(_target.IsExternalProxy ? Pages.Settings : Pages.Add);
            return;
        }

        IsWorking = true;
        try
        {
            await _gateway.ConnectAsync(server, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            IsWorking = false;
        }

        // В строгом режиме и при загрузке отказ оставляет «Блокировку» с тем же кодом.
        if (_gateway.Status is { State: ConnectionState.Error or ConnectionState.Blocked, ErrorCode: ErrorCodes.OtherVpn } status)
        {
            Others = status.OtherClients;
            _toast.Show(Loc.Instance["problem.conflict.still"], ToastViewModel.LongDuration);
            return;
        }

        _navigator.Navigate(Pages.Home);
    }

    private bool CanWork() => !IsWorking;

    private bool CanInstallNow() => CanWork() && CanInstall;

    private bool CanCheckNow() => CanWork() && !IsCheckingAccess;

    /// <summary>Только чтение разрешения ОС: ошибка проверки не означает запрет или отсутствие установки.</summary>
    public async Task RefreshServiceAccessAsync()
    {
        if (!IsServiceError || !_access.IsSupported)
        {
            return;
        }

        var read = ++_accessRead;
        IsCheckingAccess = true;
        try
        {
            var result = await Task.Run(_access.Read).ConfigureAwait(true);
            if (read == _accessRead)
            {
                Registration = result;
            }
        }
        finally
        {
            if (read == _accessRead)
            {
                IsCheckingAccess = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void OpenServiceSettings()
    {
        if (!_access.OpenSettings())
        {
            _toast.Show(Loc.Instance["problem.background.open_failed"], ToastViewModel.LongDuration);
        }
    }

    /// <summary>Повторный поиск IPC, без установки службы и без подключения к VPN-серверу.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckServiceAsync()
    {
        IsWorking = true;
        try
        {
            await RefreshServiceAccessAsync().ConfigureAwait(true);
            if (NeedsApproval)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await _gateway.GetDetailsAsync(timeout.Token).ConfigureAwait(true) is not null && _gateway.Service == ServiceKind.Installed)
            {
                _navigator.Navigate(Pages.Home);
                return;
            }

            _toast.Show(Loc.Instance["problem.connection.retry_failed"], ToastViewModel.LongDuration);
        }
        catch (OperationCanceledException)
        {
            _toast.Show(Loc.Instance["problem.connection.retry_failed"], ToastViewModel.LongDuration);
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>
    /// Системный прокси пользователя — в фоне: на Linux и macOS его читает отдельная программа. «Работает другой
    /// VPN» бывает только в режиме VPN, а там системный прокси служба не ставит — он всегда чужой.
    /// </summary>
    private async Task ReadProxyAsync() => ForeignProxy = await Task.Run(_userProxy.Read).ConfigureAwait(true);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallProgressText))]
    public partial int InstallPercent { get; private set; }

    public string InstallProgressText => ServiceSetupProgress.Text(InstallPercent);

    [ObservableProperty]
    public partial bool IsInstallingService { get; private set; }


    [RelayCommand]
    private void Cancel() => _navigator.Navigate(Pages.Home);

    /// <summary>Журнал — на экране «Проверка»: там записи окна о поиске и запуске службы.</summary>
    [RelayCommand]
    private void Details() => _navigator.Navigate(Pages.Check);
}
