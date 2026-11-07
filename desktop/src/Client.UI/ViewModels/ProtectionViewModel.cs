using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Состояние защиты для кнопки-щита и подписи под ней. Одно на все экраны: островок с кнопкой
/// виден везде. Состояние приходит только от службы — интерфейс его не придумывает.
/// </summary>
public sealed partial class ProtectionViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ConnectionController _controller;
    private readonly OverlayViewModel _overlay;
    private readonly SettingsService _settings;
    private readonly ConnectionTarget _target;

    public ProtectionViewModel(
        IGatewayClient gateway,
        IServerCatalog catalog,
        ConnectionController controller,
        OverlayViewModel overlay,
        SettingsService settings,
        ConnectionTarget target)
    {
        _gateway = gateway;
        _controller = controller;
        _overlay = overlay;
        _settings = settings;
        _target = target;
        Status = gateway.Status;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => Status = status);
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        target.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone), nameof(Label), nameof(Mark), nameof(IsBusy), nameof(ActionText), nameof(StateKey), nameof(Title), nameof(HasNetworkProblem))]
    public partial ConnectionStatus Status { get; private set; }

    public Tone Tone => HasNetworkProblem
        ? Status.Mode == ConnectionMode.Vpn && Status.Protection.KillSwitch == CheckState.Ok
            && Status.Protection.AllTraffic is not (CheckState.Off or CheckState.Warning) ? Tone.Blocked : Tone.Error
        : ToneOf.State(Status);

    public bool HasNetworkProblem => NotificationPolicy.HasNetworkProblem(Status);

    /// <summary>Ключ текстов состояния: «paused», «proxy», «strict» или имя состояния — для главного экрана и трея.</summary>
    public string StateKey => Status.IsPaused ? "paused" : Status.IsProxy ? "proxy" : Status.HasExitIpLeak ? "leak" : Status.StrictBlock ? "strict" : Status.State.ToString().ToLowerInvariant();

    /// <summary>Заголовок состояния: «IP скрыт», «Прокси включен», «Защита на паузе».</summary>
    public string Title => HasNetworkProblem ? Loc.Instance["status.offline.title"]
        : Status is { State: ConnectionState.Error, ErrorCode: ErrorCodes.OtherVpn } ? Loc.Instance["problem.conflict.title"]
        : Loc.Instance[$"status.{StateKey}.title"];

    public bool HasServer => _target.Selected is not null;

    /// <summary>Неготовая цель подключения открывает ее настройки, а не подставляет сервер другого режима.</summary>
    public event EventHandler? ConfigureTargetRequested;

    /// <summary>Подпись под кнопкой.</summary>
    public string Label => HasNetworkProblem ? Loc.Instance["power.label.offline"]
        : Status.HasExitIpLeak ? Loc.Instance["power.label.leak"]
        : Status.IsPaused ? Loc.Instance["power.label.paused"]
        : Status.IsProxy ? Loc.Instance["power.label.proxy"]
        : !HasServer && Status.State == ConnectionState.Off ? Loc.Instance[_target.IsExternalProxy ? "externalProxy.power.label" : "power.label.noServer"]
        : Loc.Instance["power.label." + Status.State.ToString().ToLowerInvariant()];

    /// <summary>Знак внутри щита: ключ иконки Icon.Mark.*.</summary>
    public string Mark => HasNetworkProblem || Status.IsPaused || Status.IsProxy || Status.HasExitIpLeak ? "Icon.Mark.Alert" : Status.State switch
    {
        ConnectionState.Connected => "Icon.Mark.Ok",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "Icon.Mark.Wait",
        ConnectionState.Blocked or ConnectionState.Error => "Icon.Mark.Alert",
        _ => "Icon.Mark.Off",
    };

    /// <summary>Крутится кольцо ожидания.</summary>
    public bool IsBusy => !HasNetworkProblem && Status.State is ConnectionState.Connecting or ConnectionState.Reconnecting;

    /// <summary>Что сделает нажатие — подсказка и имя для чтения с экрана. На паузе строгой «Блокировки» — подключить.</summary>
    public string ActionText => !HasServer && _target.IsExternalProxy && ConnectionController.WantsConnect(Status) ? Loc.Instance["externalProxy.power.configure"]
        : Status is { IsPaused: true, StrictBlock: false } ? Loc.Instance["power.action.resume"]
        : ConnectionController.WantsConnect(Status) ? Loc.Instance["power.action.connect"]
        : Loc.Instance["power.action.disconnect"];

    // Нажатие доступно и во время подключения — им подключение и прерывают.
    // Если отключение снимет блок — сначала спросить (доска 18). Включенный Kill Switch
    // оставляет сеть закрытой, поэтому вопрос о раскрытии IP не нужен. Из ручного блока щит подключает VPN.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task<bool> ToggleAsync()
    {
        if (Status is { State: ConnectionState.Blocked, IsPaused: false, StrictBlock: false } && !ConnectionController.KeepsBlock(_settings.Current))
        {
            var dialog = new DialogViewModel(
                DialogKind.Warning,
                "Icon.Shield",
                "Icon.Mark.Alert",
                Loc.Instance["dialog.unprotect.title"],
                Loc.Instance["dialog.unprotect.text"],
                Loc.Instance["dialog.unprotect.keep"],
                Loc.Instance["dialog.unprotect.drop"])
            { IsSecondaryDanger = true };
            if (await _overlay.AskAsync(dialog).ConfigureAwait(true) != DialogChoice.Secondary)
            {
                return true;
            }
        }

        var connected = await _controller.ToggleAsync().ConfigureAwait(true);
        if (!connected)
        {
            ConfigureTargetRequested?.Invoke(this, EventArgs.Empty);
        }

        return connected;
    }

    private void Refresh() => OnPropertyChanged(string.Empty);
}
