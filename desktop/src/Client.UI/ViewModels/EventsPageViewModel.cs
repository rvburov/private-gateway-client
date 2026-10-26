using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «События сети» — сетевая лаборатория (этап 8, экрана в макете нет — по дизайн-системе): что произошло с защитой и сетью
/// с запуска окна (<see cref="NetworkEventLog"/>) и почему — простыми словами, а в режиме эксперта еще и что служба
/// сделала с маршрутами, DNS и брандмауэром. Новые — сверху.
/// </summary>
public sealed partial class EventsPageViewModel : ObservableObject
{
    private readonly NetworkEventLog _log;
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;

    public EventsPageViewModel(NetworkEventLog log, SettingsService settings, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        _log = log;
        _settings = settings;
        _navigator = navigator;
        log.Added += (_, item) => Dispatcher.UIThread.Post(() =>
        {
            Rows.Insert(0, new EventRowViewModel(item, IsExpert));
            OnPropertyChanged(nameof(HasEvents));
        });
        Loc.Instance.LanguageChanged += (_, _) => Show();
        settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsExpert));
            OnPropertyChanged(nameof(IsBeginner));
            Show();
        };
        Show();
    }

    /// <summary>События — новые сверху.</summary>
    public ObservableCollection<EventRowViewModel> Rows { get; } = [];

    public bool HasEvents => Rows.Count > 0;

    /// <summary>Объяснения для эксперта — общий режим лаборатории.</summary>
    public bool IsExpert
    {
        get => _settings.Current.LabExpert;
        set => _settings.Update(s => s with { LabExpert = value });
    }

    public bool IsBeginner
    {
        get => !IsExpert;
        set => IsExpert = !value;
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show()
    {
        Rows.Clear();
        foreach (var item in _log.Events.Reverse())
        {
            Rows.Add(new EventRowViewModel(item, IsExpert));
        }

        OnPropertyChanged(nameof(HasEvents));
    }
}

/// <summary>Событие для экрана: время, тон (как у проверок), заголовок, подробность, объяснение и — эксперту — что под капотом.</summary>
public sealed class EventRowViewModel
{
    public EventRowViewModel(NetworkEvent item, bool expert)
    {
        ArgumentNullException.ThrowIfNull(item);
        var loc = Loc.Instance;
        var key = "event." + char.ToLowerInvariant(item.Kind.ToString()[0]) + item.Kind.ToString()[1..];
        Kind = item.Kind;
        Time = item.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Title = item.Kind switch
        {
            NetworkEventKind.Reconnecting or NetworkEventKind.ExitMeasured => loc.Format(key + ".title", item.Detail ?? "?"),
            _ => loc[key + ".title"],
        };
        Text = item.Kind == NetworkEventKind.Warning
            ? loc.Format(key + ".text", loc[key + "." + (item.Detail ?? "allTraffic")])
            : loc[key + ".text"];
        Detail = item.Kind is NetworkEventKind.Blocked or NetworkEventKind.Error or NetworkEventKind.StrictBlock or NetworkEventKind.NetworkChanged
            ? item.Detail
            : null;
        Expert = expert ? loc[key + ".expert"] : null;
        State = item.Kind switch
        {
            NetworkEventKind.Connected or NetworkEventKind.Resumed or NetworkEventKind.ExitMeasured => CheckState.Ok,
            NetworkEventKind.Reconnecting or NetworkEventKind.Blocked or NetworkEventKind.Error or NetworkEventKind.StrictBlock
                or NetworkEventKind.Paused or NetworkEventKind.Warning or NetworkEventKind.ProxyOn => CheckState.Warning,
            NetworkEventKind.Connecting => CheckState.Waiting,
            _ => CheckState.Off,
        };
        Icon = State switch
        {
            CheckState.Ok => "Icon.Mark.Ok",
            CheckState.Warning => "Icon.Mark.Alert",
            CheckState.Waiting => "Icon.Mark.Wait",
            _ => "Icon.Mark.Off",
        };
    }

    public NetworkEventKind Kind { get; }

    /// <summary>Когда — по часам машины: «14:02:31».</summary>
    public string Time { get; }

    public string Title { get; }

    /// <summary>Код ошибки или имя сети; нет — null.</summary>
    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string Text { get; }

    public string? Expert { get; }

    public bool HasExpert => Expert is not null;

    /// <summary>Тон — как у строк «Проверки»: в порядке, ждет, внимание, выключено.</summary>
    public CheckState State { get; }

    public string Icon { get; }
}
