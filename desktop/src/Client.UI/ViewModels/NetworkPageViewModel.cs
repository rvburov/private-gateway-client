using System.Collections.ObjectModel;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Сеть машины» — сетевой инспектор (этап 7, экрана в макете нет — по дизайн-системе): адаптеры с адресами, шлюзами, DNS,
/// DHCP и MAC и маршруты. Снимок делает служба (окно сеть не трогает) при открытии и по «Обновить»; инспектор только
/// читает. Подстраница «Проверки»: островок виден, «назад» — к проверке.
/// </summary>
public sealed partial class NetworkPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private NetworkSnapshot? _snapshot;

    public NetworkPageViewModel(IGatewayClient gateway, INavigator navigator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _navigator = navigator;
        _time = time;
        Loc.Instance.LanguageChanged += (_, _) => Show(_snapshot);
        _ = RefreshAsync();
    }

    public ObservableCollection<AdapterCardViewModel> Adapters { get; } = [];

    public ObservableCollection<RouteRowViewModel> Routes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshText))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Служба не ответила (ее нет или она старая) — снимка нет.</summary>
    [ObservableProperty]
    public partial bool IsUnavailable { get; private set; }

    /// <summary>«Снимок в 18:10» — когда служба прочитала сеть.</summary>
    [ObservableProperty]
    public partial string TakenText { get; private set; } = string.Empty;

    public string RefreshText => Loc.Instance[IsLoading ? "network.refreshing" : "network.refresh"];

    public bool HasRoutes => Routes.Count > 0;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var snapshot = await _gateway.InspectNetworkAsync().ConfigureAwait(true);
            IsUnavailable = snapshot is null;
            Show(snapshot ?? _snapshot);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show(NetworkSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Adapters.Clear();
        Routes.Clear();
        OnPropertyChanged(nameof(RefreshText));
        if (snapshot is null)
        {
            TakenText = string.Empty;
            OnPropertyChanged(nameof(HasRoutes));
            return;
        }

        TakenText = Loc.Instance.Format("network.taken", TimeText.Day(snapshot.TakenAt, _time));
        foreach (var card in snapshot.Cards.OrderBy(Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Adapters.Add(new AdapterCardViewModel(card));
        }

        // Маршрут по умолчанию — первым: по нему идет весь трафик, которому нет маршрута точнее.
        foreach (var route in snapshot.Routes.OrderBy(r => r.Destination == "default" ? 0 : 1))
        {
            Routes.Add(new RouteRowViewModel(route));
        }

        OnPropertyChanged(nameof(HasRoutes));
    }

    // Сначала включенные карты, потом туннели, остальные включенные, выключенные, петля — последней.
    private static int Order(NetworkCard card) => card switch
    {
        { Kind: AdapterKind.Loopback } => 5,
        { IsUp: false } => 4,
        { Kind: AdapterKind.Ethernet or AdapterKind.Wifi or AdapterKind.Cellular } => 0,
        { Kind: AdapterKind.Tunnel } => 1,
        _ => 2,
    };
}

/// <summary>Карточка адаптера: заголовок (вид, имя, описание, состояние) и строки сведений — только те, что известны.</summary>
public sealed class AdapterCardViewModel
{
    public AdapterCardViewModel(NetworkCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Name = card.Name;
        Description = card.Description;
        Kind = card.Kind;
        IsUp = card.IsUp;
        Icon = card.Kind switch
        {
            AdapterKind.Wifi => "Icon.Net.WiFi",
            AdapterKind.Cellular => "Icon.Net.Mobile",
            AdapterKind.Tunnel => "Icon.Lock",
            AdapterKind.Loopback => "Icon.Refresh",
            AdapterKind.Other => "Icon.Net.Usb",
            _ => "Icon.Net.Ethernet",
        };
        KindText = Loc.Instance[$"network.kind.{card.Kind.ToString().ToLowerInvariant()}"];
        StatusText = Loc.Instance[card.IsUp ? "network.status.up" : "network.status.down"];

        var rows = new List<DetailRowViewModel>();
        void Add(string key, IEnumerable<string> values)
        {
            var text = string.Join('\n', values);
            if (text.Length > 0)
            {
                rows.Add(new DetailRowViewModel(Loc.Instance[key], text));
            }
        }

        Add("network.ipv4", card.Addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));
        Add("network.ipv6", card.Addresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()));
        Add("network.gateway", card.Gateways.Select(g => g.ToString()));
        Add("network.dns", card.DnsServers.Select(d => d.ToString()));
        if (card.IsUp && card.Kind is not (AdapterKind.Loopback or AdapterKind.Tunnel)
            && card.Addresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            Add("network.dhcp", [Dhcp(card.Dhcp)]);
        }

        Add("network.mac", card.Mac is { } mac ? [mac] : []);
        Add("network.link", Link(card) is { Length: > 0 } link ? [link] : []);
        Rows = rows;
    }

    public string Name { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public AdapterKind Kind { get; }

    public bool IsUp { get; }

    /// <summary>Ключ значка вида адаптера в ресурсах.</summary>
    public string Icon { get; }

    public string KindText { get; }

    /// <summary>Под именем: вид и описание драйвера — «Wi-Fi · Intel(R) Wi-Fi 6 AX201».</summary>
    public string Subtitle => HasDescription ? $"{KindText} · {Description}" : KindText;

    public string StatusText { get; }

    public IReadOnlyList<DetailRowViewModel> Rows { get; }

    /// <summary>DHCP: «автоматически · сервер 192.0.2.1 · осталось 20 ч» или «вручную».</summary>
    public static string Dhcp(DhcpLease? lease)
    {
        if (lease is null)
        {
            return Loc.Instance["network.dhcp.manual"];
        }

        var parts = new List<string> { Loc.Instance["network.dhcp.auto"] };
        if (lease.Server is { } server)
        {
            parts.Add(Loc.Instance.Format("network.dhcp.server", server));
        }

        if (lease.Remaining is { } left)
        {
            parts.Add(left < TimeSpan.FromHours(1) ? Loc.Instance.Format("network.left.minutes", Math.Max(1, (int)left.TotalMinutes))
                : left < TimeSpan.FromDays(2) ? Loc.Instance.Format("network.left.hours", (int)left.TotalHours)
                : Loc.Instance.Format("network.left.days", (int)left.TotalDays));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Канал: «MTU 1500 · 866 Мбит/с»; неизвестное — без него.</summary>
    public static string Link(NetworkCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var parts = new List<string>();
        if (card.Mtu is { } mtu)
        {
            parts.Add(Loc.Instance.Format("network.mtu", mtu));
        }

        if (card.SpeedBitsPerSecond is { } speed)
        {
            parts.Add(speed >= 1_000_000_000 && speed % 1_000_000_000 == 0
                ? Loc.Instance.Format("network.speed.gbit", speed / 1_000_000_000)
                : Loc.Instance.Format("network.speed.mbit", (speed / 1_000_000).ToString("N0", Loc.Instance.Culture)));
        }

        return string.Join(" · ", parts);
    }
}

/// <summary>Строка сведений: подпись и значение (несколько адресов — по строке на адрес).</summary>
public sealed record DetailRowViewModel(string Label, string Value);

/// <summary>Маршрут: куда, через что, метрика.</summary>
public sealed class RouteRowViewModel(NetworkRoute route)
{
    public string Destination { get; } = route.Destination == "default" ? Loc.Instance["network.route.default"] : route.Destination;

    /// <summary>«через 192.0.2.1 · Wi-Fi» или просто адаптер — сеть рядом.</summary>
    public string Via { get; } = route.Gateway is { } gateway
        ? Loc.Instance.Format("network.route.via", gateway, route.Interface)
        : route.Interface;

    public string Metric { get; } = route.Metric is { } metric ? Loc.Instance.Format("network.route.metric", metric) : string.Empty;

    public bool HasMetric => Metric.Length > 0;
}
