using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
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
    private readonly IServerCatalog _catalog;
    private readonly ConnectionController _controller;
    private readonly OverlayViewModel _overlay;

    public ProtectionViewModel(
        IGatewayClient gateway,
        IServerCatalog catalog,
        ConnectionController controller,
        OverlayViewModel overlay)
    {
        _gateway = gateway;
        _catalog = catalog;
        _controller = controller;
        _overlay = overlay;
        Status = gateway.Status;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => Status = status);
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone), nameof(Label), nameof(Mark), nameof(IsBusy), nameof(ActionText), nameof(StateKey), nameof(Title))]
    public partial ConnectionStatus Status { get; private set; }

    public Tone Tone => ToneOf.State(Status);

    /// <summary>Ключ текстов состояния: «paused», «proxy» или имя состояния — для главного экрана и трея.</summary>
    public string StateKey => Status.IsPaused ? "paused" : Status.IsProxy ? "proxy" : Status.State.ToString().ToLowerInvariant();

    /// <summary>Заголовок состояния: «IP скрыт», «Прокси включен», «Защита на паузе».</summary>
    public string Title => Loc.Instance[$"status.{StateKey}.title"];

    public bool HasServer => _catalog.Selected is not null;

    /// <summary>Подпись под кнопкой.</summary>
    public string Label => Status.IsPaused ? Loc.Instance["power.label.paused"]
        : Status.IsProxy ? Loc.Instance["power.label.proxy"]
        : !HasServer && Status.State == ConnectionState.Off ? Loc.Instance["power.label.noServer"]
        : Loc.Instance["power.label." + Status.State.ToString().ToLowerInvariant()];

    /// <summary>Знак внутри щита: ключ иконки Icon.Mark.*.</summary>
    public string Mark => Status.IsPaused || Status.IsProxy ? "Icon.Mark.Alert" : Status.State switch
    {
        ConnectionState.Connected => "Icon.Mark.Ok",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "Icon.Mark.Wait",
        ConnectionState.Blocked or ConnectionState.Error => "Icon.Mark.Alert",
        _ => "Icon.Mark.Off",
    };

    /// <summary>Крутится кольцо ожидания.</summary>
    public bool IsBusy => Status.State is ConnectionState.Connecting or ConnectionState.Reconnecting;

    /// <summary>Что сделает нажатие — подсказка и имя для чтения с экрана.</summary>
    public string ActionText => Status.IsPaused ? Loc.Instance["power.action.resume"]
        : ConnectionController.WantsConnect(Status.State) ? Loc.Instance["power.action.connect"]
        : Loc.Instance["power.action.disconnect"];

    // Нажатие доступно и во время подключения — им подключение и прерывают.
    // При блокировке снять защиту — значит открыть сайтам реальный IP: сначала спросить (доска 18).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task<bool> ToggleAsync()
    {
        if (Status.State == ConnectionState.Blocked && !Status.IsPaused)
        {
            var dialog = new DialogViewModel(
                DialogKind.Warning,
                "Icon.Shield",
                "Icon.Mark.Alert",
                Loc.Instance["dialog.unprotect.title"],
                Loc.Instance["dialog.unprotect.text"],
                Loc.Instance["dialog.unprotect.keep"],
                Loc.Instance["dialog.unprotect.drop"]) { IsSecondaryDanger = true };
            if (await _overlay.AskAsync(dialog).ConfigureAwait(true) != DialogChoice.Secondary)
            {
                return true;
            }
        }

        return await _controller.ToggleAsync().ConfigureAwait(true);
    }

    private void Refresh() => OnPropertyChanged(string.Empty);
}
