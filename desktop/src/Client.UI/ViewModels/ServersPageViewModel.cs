using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Серверы» по макету (доска 2): поиск, вкладки «Все · Избранные · Недавние», фильтр протоколов
/// и сортировка, строки с пингом. Сервер меняется только нажатием на строку.
/// </summary>
public sealed partial class ServersPageViewModel : ObservableObject
{
    /// <summary>Пинг выше этого — серым, как в макете: сервер работает, но медленно.</summary>
    public const int SlowLatencyMs = 120;

    private readonly IServerStore _store;
    private readonly IClipboardService _clipboard;
    private readonly IServerCatalog _catalog;
    private readonly ConnectionController _controller;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;
    private readonly OverlayViewModel _overlay;
    private readonly HashSet<ProxyProtocol> _hidden = [];

    public ServersPageViewModel(
        ProtectionViewModel protection,
        IServerCatalog catalog,
        IServerStore store,
        IClipboardService clipboard,
        ConnectionController controller,
        INavigator navigator,
        ToastViewModel toast,
        OverlayViewModel overlay)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(catalog);
        Protection = protection;
        _catalog = catalog;
        _store = store;
        _clipboard = clipboard;
        _controller = controller;
        _navigator = navigator;
        _toast = toast;
        _overlay = overlay;
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();
        Protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                Rebuild();
            }
        };
        Rebuild();
    }

    public ProtectionViewModel Protection { get; }

    // ── Поиск и вкладки ──────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Rebuild();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllTab), nameof(IsFavoritesTab), nameof(IsRecentTab))]
    public partial ServerListTab Tab { get; set; }

    partial void OnTabChanged(ServerListTab value) => Rebuild();

    public bool IsAllTab
    {
        get => Tab == ServerListTab.All;
        set => PickTab(value, ServerListTab.All);
    }

    public bool IsFavoritesTab
    {
        get => Tab == ServerListTab.Favorites;
        set => PickTab(value, ServerListTab.Favorites);
    }

    public bool IsRecentTab
    {
        get => Tab == ServerListTab.Recent;
        set => PickTab(value, ServerListTab.Recent);
    }

    public string AllTabText => Loc.Instance.Format("servers.tab.all", _catalog.Servers.Count);

    // ── Фильтр и сортировка ──────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    public partial bool IsFilterOpen { get; set; }

    public ObservableCollection<ProtocolFilterViewModel> Protocols { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSortByLatency), nameof(IsSortByName))]
    public partial ServerSort Sort { get; set; }

    partial void OnSortChanged(ServerSort value) => Rebuild();

    public bool IsSortByLatency
    {
        get => Sort == ServerSort.Latency;
        set
        {
            if (value)
            {
                Sort = ServerSort.Latency;
            }
        }
    }

    public bool IsSortByName
    {
        get => Sort == ServerSort.Name;
        set
        {
            if (value)
            {
                Sort = ServerSort.Name;
            }
        }
    }

    /// <summary>Сколько настроек фильтра отличается от обычных: скрытые протоколы и порядок не по пингу.</summary>
    public int FilterCount => Protocols.Count(p => !p.IsOn) + (Sort == ServerSort.Latency ? 0 : 1);

    public string FilterCountText => FilterCount.ToString(CultureInfo.InvariantCulture);

    public bool HasFilterCount => FilterCount > 0;

    /// <summary>Кнопка фильтра подсвечена, пока панель открыта или фильтр что-то скрывает.</summary>
    public bool IsFilterActive => IsFilterOpen || HasFilterCount;

    // ── Список ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial IReadOnlyList<ServerGroupViewModel> Groups { get; private set; } = [];

    public bool HasRows => Groups.Count > 0;

    /// <summary>Пояснение вместо пустого списка; null — список не пуст.</summary>
    public string? EmptyText => HasRows
        ? null
        : _catalog.Servers.Count == 0
            ? Loc.Instance["servers.none"]
            : SearchText.Trim().Length == 0 && FilterCount == 0 && Tab != ServerListTab.All
                ? Loc.Instance[Tab == ServerListTab.Favorites ? "servers.empty.favorites" : "servers.empty.recent"]
                : Loc.Instance["servers.empty"];

    public bool IsEmpty => !HasRows;

    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            await _catalog.RefreshAsync().ConfigureAwait(true);
            _toast.Show(Loc.Instance["server.refreshed"]);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void Add() => _navigator.Navigate(Pages.Add);

    [RelayCommand]
    private void ToggleFilter() => IsFilterOpen = !IsFilterOpen;

    [RelayCommand]
    private void CloseFilter() => IsFilterOpen = false;

    [RelayCommand]
    private void ResetFilter()
    {
        _hidden.Clear();
        foreach (var protocol in Protocols)
        {
            protocol.SetQuietly(true);
        }

        Sort = ServerSort.Latency;
        Rebuild();
    }

    /// <summary>Нажатие на строку: выбрать сервер, при включенной защите — переподключиться к нему.</summary>
    [RelayCommand]
    private Task ChooseAsync(string serverId) => _controller.SwitchServerAsync(serverId);

    /// <summary>«⋯» в строке: карточка сервера нижним листом поверх окна.</summary>
    [RelayCommand]
    private void OpenCard(string serverId)
    {
        IsFilterOpen = false;
        _overlay.ShowSheet(new ServerCardViewModel(serverId, _catalog, _store, _clipboard, Protection, _controller, _overlay, _toast));
    }

    private void PickTab(bool picked, ServerListTab tab)
    {
        if (picked)
        {
            Tab = tab;
        }
    }

    internal void OnProtocolToggled(ProxyProtocol protocol, bool isOn)
    {
        if (isOn)
        {
            _hidden.Remove(protocol);
        }
        else
        {
            _hidden.Add(protocol);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        var servers = _catalog.Servers;
        SyncProtocols(servers);
        var query = new ServerListQuery(SearchText, Tab, _hidden, Sort);
        var selectedId = _catalog.Selected?.Id;
        var connected = Protection.Status.State == ConnectionState.Connected;
        Groups = ServerList.Build(servers, _catalog.Subscriptions, query, Loc.Instance.Culture)
            .Select(g => new ServerGroupViewModel(
                GroupTitle(g),
                Tab == ServerListTab.All ? UpdatedText(g.Subscription) : null,
                g.Servers.Select(s => Row(s, s.Id == selectedId, connected)).ToList()))
            .ToList();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Флажки протоколов — только тех, что есть среди серверов.</summary>
    private void SyncProtocols(IReadOnlyList<ServerProfile> servers)
    {
        var present = ServerList.ProtocolsIn(servers);
        if (present.SequenceEqual(Protocols.Select(p => p.Protocol)))
        {
            return;
        }

        Protocols.Clear();
        foreach (var protocol in present)
        {
            Protocols.Add(new ProtocolFilterViewModel(protocol, !_hidden.Contains(protocol), this));
        }
    }

    private string GroupTitle(ServerGroup group) => Tab switch
    {
        ServerListTab.Favorites => Loc.Instance.Format("servers.group.favorites", group.Servers.Count),
        ServerListTab.Recent => Loc.Instance.Format("servers.group.recent", group.Servers.Count),
        _ when group.Subscription is { } s => Loc.Instance.Format("servers.group.subscription", s.Name, group.Servers.Count),
        _ => Loc.Instance.Format("servers.group.manual", group.Servers.Count),
    };

    private static string? UpdatedText(ServerSubscription? subscription) => subscription switch
    {
        null => null,
        { UpdatedAt: { } at } => Loc.Instance.Format("servers.updated",
            at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)),
        _ => Loc.Instance["servers.updated.never"],
    };

    private ServerRowViewModel Row(ServerProfile server, bool isSelected, bool connected) => new(
        server.Id,
        server.CountryCode,
        server.Name,
        server.Host + " · " + ServerFacts.Summary(server),
        isSelected ? Loc.Instance[connected ? "servers.tag.active" : "servers.tag.selected"] : null,
        server.LatencyMs is { } ms
            ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"]
            : Loc.Instance["servers.latency.none"],
        server.LatencyMs switch
        {
            null => LatencyKind.None,
            > SlowLatencyMs => LatencyKind.Slow,
            _ => LatencyKind.Good,
        },
        ChooseCommand,
        OpenCardCommand);
}

/// <summary>Как окрашен пинг в строке: быстрый — зеленым, медленный — серым, нет ответа — оранжевым.</summary>
public enum LatencyKind
{
    Good,
    Slow,
    None,
}

/// <summary>Группа строк: заголовок подписки или вкладки и время обновления подписки.</summary>
public sealed record ServerGroupViewModel(string Title, string? UpdatedText, IReadOnlyList<ServerRowViewModel> Rows)
{
    public bool HasUpdated => UpdatedText is not null;
}

/// <summary>Строка сервера: значок страны, название, адрес и протокол, пинг.</summary>
public sealed record ServerRowViewModel(
    string Id,
    string CountryCode,
    string Name,
    string Address,
    string? Tag,
    string LatencyText,
    LatencyKind Latency,
    IAsyncRelayCommand<string> ChooseCommand,
    IRelayCommand<string> OpenCommand)
{
    public bool IsSelected => Tag is not null;

    public bool IsSlow => Latency == LatencyKind.Slow;

    public bool IsDown => Latency == LatencyKind.None;

    /// <summary>Подсказка к «нет»: пинг показан словом и цветом, причина — в подсказке.</summary>
    public string? LatencyHint => IsDown ? Loc.Instance["servers.latency.noneHint"] : null;

    public string MoreName => Loc.Instance.Format("servers.more.name", Name);
}

/// <summary>Флажок протокола в фильтре.</summary>
public sealed partial class ProtocolFilterViewModel : ObservableObject
{
    private readonly ServersPageViewModel _owner;
    private bool _quiet;

    internal ProtocolFilterViewModel(ProxyProtocol protocol, bool isOn, ServersPageViewModel owner)
    {
        Protocol = protocol;
        _owner = owner;
        SetQuietly(isOn);
    }

    public ProxyProtocol Protocol { get; }

    public string Name => ServerFacts.ProtocolName(Protocol);

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_quiet)
        {
            _owner.OnProtocolToggled(Protocol, value);
        }
    }

    /// <summary>Поставить значение без пересборки списка — при сбросе фильтра целиком.</summary>
    internal void SetQuietly(bool isOn)
    {
        _quiet = true;
        IsOn = isOn;
        _quiet = false;
    }
}
