using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Сеть машины» — сетевой инспектор (этап 7, экрана в макете нет — по дизайн-системе): внешний IP и его провайдер,
/// адаптеры с адресами, шлюзами, DNS, DHCP и MAC и маршруты. Снимок делает служба (окно сеть не трогает) при открытии и по
/// «Обновить»; внешний IP служба меряет, а о провайдере спрашивает RIPEstat только по кнопкам. Инспектор только читает.
/// Подстраница «Проверки»: островок виден, «назад» — к проверке.
/// </summary>
public sealed partial class NetworkPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private NetworkSnapshot? _snapshot;

    public NetworkPageViewModel(IGatewayClient gateway, ProtectionViewModel protection, INavigator navigator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        _gateway = gateway;
        _protection = protection;
        _navigator = navigator;
        _time = time;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            Show(_snapshot);
            OnPropertyChanged(nameof(MeasureText));
            foreach (var address in PublicAddresses)
            {
                address.Refresh();
            }
        };
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                ShowPublic();
            }
        };
        ShowPublic();
        _ = RefreshAsync();
    }

    /// <summary>Внешние адреса, которые служба знает: через сервер (адрес выхода) и напрямую (реальный).</summary>
    public ObservableCollection<PublicAddressViewModel> PublicAddresses { get; } = [];

    public bool HasPublicAddresses => PublicAddresses.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureText))]
    public partial bool IsMeasuring { get; private set; }

    public string MeasureText => Loc.Instance[IsMeasuring ? "network.public.measuring" : "network.public.measure"];

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

    /// <summary>Внешний IP меряет служба; новый адрес приходит в состоянии защиты.</summary>
    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            return;
        }

        IsMeasuring = true;
        try
        {
            await _gateway.MeasureIpAsync().ConfigureAwait(true);
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    // Адреса из состояния защиты. Тот же адрес остается той же строкой — с уже полученным ответом о провайдере.
    private void ShowPublic()
    {
        var exit = _protection.Status.Exit;
        var wanted = new List<(string Key, IPAddress Address)>();
        if (IPAddress.TryParse(exit.Ip, out var through))
        {
            wanted.Add(("network.public.exit", through));
        }

        if (IPAddress.TryParse(exit.DirectIp, out var direct))
        {
            wanted.Add(("network.public.direct", direct));
        }

        if (wanted.SequenceEqual(PublicAddresses.Select(a => (a.LabelKey, a.Address))))
        {
            return;
        }

        var known = PublicAddresses.ToList();
        PublicAddresses.Clear();
        foreach (var (key, address) in wanted)
        {
            PublicAddresses.Add(known.FirstOrDefault(a => a.LabelKey == key && a.Address.Equals(address))
                ?? new PublicAddressViewModel(_gateway, key, address));
        }

        OnPropertyChanged(nameof(HasPublicAddresses));
    }

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

/// <summary>
/// Внешний адрес и что о нем известно в интернете. «Кто провайдер» спрашивает службу (она — RIPEstat): ответ — строки
/// «Провайдер», «Сеть», «Место» или почему сведений нет. Место — примерное, так и подписано.
/// </summary>
public sealed partial class PublicAddressViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private AddressInfo? _info;
    private bool _noService;

    public PublicAddressViewModel(IGatewayClient gateway, string labelKey, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        LabelKey = labelKey;
        Address = address;
    }

    /// <summary>Ключ подписи: «Через сервер» или «Напрямую».</summary>
    public string LabelKey { get; }

    public IPAddress Address { get; }

    public string Label => Loc.Instance[LabelKey];

    public string AddressText => Address.ToString();

    public ObservableCollection<DetailRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DescribeText), nameof(CanDescribe))]
    public partial bool IsAsking { get; private set; }

    /// <summary>Почему сведений нет; есть — пусто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; private set; } = string.Empty;

    public bool HasNote => Note.Length > 0;

    public string DescribeText => Loc.Instance[IsAsking ? "network.public.asking" : "network.public.describe"];

    /// <summary>Спросить можно, пока ответа нет или источник не ответил.</summary>
    public bool CanDescribe => !IsAsking && (_info is null || _info.Failure == AddressFailure.Unreachable);

    /// <summary>Тексты заново — после смены языка.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(DescribeText));
        Show();
    }

    [RelayCommand]
    private async Task DescribeAsync()
    {
        if (IsAsking)
        {
            return;
        }

        IsAsking = true;
        try
        {
            _info = await _gateway.DescribeAddressAsync(Address).ConfigureAwait(true);
            _noService = _info is null;
            Show();
        }
        finally
        {
            IsAsking = false;
        }
    }

    private void Show()
    {
        Rows.Clear();
        Note = _noService ? Loc.Instance["network.public.noService"]
            : _info?.Failure is { } failure ? Loc.Instance["network.public." + failure.ToString().ToLowerInvariant()]
            : string.Empty;
        if (_info is not { Failure: null } info)
        {
            OnPropertyChanged(nameof(CanDescribe));
            return;
        }

        if (info.Asn is { } asn)
        {
            Rows.Add(new DetailRowViewModel(Loc.Instance["network.public.provider"], info.Holder is { } holder ? $"AS{asn} · {holder}" : $"AS{asn}"));
        }

        if (info.Prefix is { } prefix)
        {
            Rows.Add(new DetailRowViewModel(Loc.Instance["network.public.prefix"], prefix));
        }

        if (info.Country is { } country)
        {
            Rows.Add(new DetailRowViewModel(Loc.Instance["network.public.place"],
                Loc.Instance.Format("network.public.approx", info.City is { } city ? $"{country} · {city}" : country)));
        }

        OnPropertyChanged(nameof(CanDescribe));
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
