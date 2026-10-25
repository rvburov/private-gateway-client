using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Строка фактов на экране ошибки: что проверено и что вышло; кружок — красный, оранжевый или серый.</summary>
public sealed record ProblemFact(string Label, string Value, bool IsBad = false, bool IsWarning = false);

/// <summary>
/// Ошибки службы по макету (доска 16): «Служба не запущена» (PGC-SVC-001) и «Нужны права
/// администратора» (PGC-SVC-004 — работает служба пользователя, а просили VPN). Выход — режим прокси
/// через службу пользователя: окно запускает ее само. Окно при этом работает: островок виден.
/// </summary>
public sealed partial class ProblemPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly SettingsService _settings;
    private readonly IServerCatalog _catalog;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;

    public ProblemPageViewModel(IGatewayClient gateway, SettingsService settings, IServerCatalog catalog, INavigator navigator, ToastViewModel toast)
    {
        _gateway = gateway;
        _settings = settings;
        _catalog = catalog;
        _navigator = navigator;
        _toast = toast;
        Code = gateway.Status.ErrorCode is ErrorCodes.AdminRequired ? ErrorCodes.AdminRequired : ErrorCodes.ServiceNotRunning;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() =>
        {
            if (status is { State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning or ErrorCodes.AdminRequired })
            {
                Code = status.ErrorCode;
            }
        });
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    /// <summary>Код ошибки, которую показывает экран; меняется вместе с новой ошибкой службы.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAdmin), nameof(Title), nameof(Text), nameof(Facts), nameof(HasCancel), nameof(DetailsText))]
    public partial string Code { get; private set; }

    /// <summary>«Нужны права администратора» — оранжевый замок; иначе «Служба не запущена» — красный щит.</summary>
    public bool IsAdmin => Code == ErrorCodes.AdminRequired;

    public string Title => Loc.Instance[IsAdmin ? "problem.admin.title" : "problem.service.title"];

    public string Text => Loc.Instance[IsAdmin ? "problem.admin.text" : "problem.service.text"];

    public IReadOnlyList<ProblemFact> Facts => IsAdmin
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

    /// <summary>«Подробнее в журнале · код PGC-SVC-001».</summary>
    public string DetailsText => Loc.Instance.Format("problem.details", Code);

    /// <summary>Идет запуск службы пользователя и подключение.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseProxyCommand))]
    public partial bool IsWorking { get; private set; }

    /// <summary>
    /// «Работать в режиме прокси»: режим — прокси, подключение к выбранному серверу; службы нет — окно
    /// запускает службу пользователя. Сервера нет — сначала добавить. Не вышло — подсказка, экран остается.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUseProxy))]
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

    private bool CanUseProxy() => !IsWorking;

    [RelayCommand]
    private void Cancel() => _navigator.Navigate(Pages.Home);

    /// <summary>Журнал — на экране «Проверка»: там записи окна о поиске и запуске службы.</summary>
    [RelayCommand]
    private void Details() => _navigator.Navigate(Pages.Check);
}
