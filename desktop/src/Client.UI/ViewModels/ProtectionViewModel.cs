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

    public ProtectionViewModel(IGatewayClient gateway, IServerCatalog catalog, ConnectionController controller)
    {
        _gateway = gateway;
        _catalog = catalog;
        _controller = controller;
        Status = gateway.Status;
        gateway.StatusChanged += (_, status) => Dispatcher.UIThread.Post(() => Status = status);
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone), nameof(Label), nameof(Mark), nameof(IsBusy), nameof(ActionText))]
    public partial ConnectionStatus Status { get; private set; }

    public bool IsSimulated => _gateway.IsSimulated;

    public Tone Tone => ToneOf.State(Status);

    public bool HasServer => _catalog.Selected is not null;

    /// <summary>Подпись под кнопкой.</summary>
    public string Label => !HasServer && Status.State == ConnectionState.Off
        ? Loc.Instance["power.label.noServer"]
        : Loc.Instance["power.label." + Status.State.ToString().ToLowerInvariant()];

    /// <summary>Знак внутри щита: ключ иконки Icon.Mark.*.</summary>
    public string Mark => Status.State switch
    {
        ConnectionState.Connected => "Icon.Mark.Ok",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "Icon.Mark.Wait",
        ConnectionState.Blocked or ConnectionState.Error => "Icon.Mark.Alert",
        _ => "Icon.Mark.Off",
    };

    /// <summary>Крутится кольцо ожидания.</summary>
    public bool IsBusy => Status.State is ConnectionState.Connecting or ConnectionState.Reconnecting;

    /// <summary>Что сделает нажатие — подсказка и имя для чтения с экрана.</summary>
    public string ActionText => ConnectionController.WantsConnect(Status.State)
        ? Loc.Instance["power.action.connect"]
        : Loc.Instance["power.action.disconnect"];

    // Нажатие доступно и во время подключения — им подключение и прерывают.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task<bool> ToggleAsync() => _controller.ToggleAsync();

    private void Refresh() => OnPropertyChanged(string.Empty);
}
