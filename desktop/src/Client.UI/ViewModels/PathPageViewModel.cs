using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Путь до сайта» — сетевая лаборатория (этап 8, экрана в макете нет — по дизайн-системе): карта устройство → адаптер →
/// роутер → провайдер → VPN-сервер → сайт из сведений службы. Звено и участок помечены «измерено» или «условно»: модель
/// не выдается за измерение. «Проверить сайт» — рукопожатие TLS и трассировка до сайта, «Кто провайдеры» — RIPEstat
/// через службу; без этих нажатий провайдер и сайт — условно.
/// </summary>
public sealed partial class PathPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly INavigator _navigator;
    private readonly SettingsService _settings;
    private NetworkSnapshot? _snapshot;
    private AddressInfo? _provider;
    private AddressInfo? _exit;
    private string? _checkedSite;
    private ProbeStep? _siteCheck;
    private IReadOnlyList<ProbeStep>? _trace;

    public PathPageViewModel(IGatewayClient gateway, ProtectionViewModel protection, SettingsService settings, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(settings);
        _gateway = gateway;
        _protection = protection;
        _settings = settings;
        _navigator = navigator;
        settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsExpert));
            OnPropertyChanged(nameof(IsBeginner));
            Show();
        };
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            Show();
            OnPropertyChanged(nameof(Note));
            OnPropertyChanged(nameof(CheckText));
            OnPropertyChanged(nameof(LookupText));
        };
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                Show();
                OnPropertyChanged(nameof(Note));
            }
        };
        Show();
    }

    /// <summary>Звенья и участки по порядку: звено, участок, звено…</summary>
    public ObservableCollection<object> Steps { get; } = [];

    /// <summary>Подробности для эксперта: сети провайдеров, узлы трассировки, канал адаптера, TLS сайта — общий режим лаборатории.</summary>
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

    /// <summary>Сайт для карты и проверки.</summary>
    [ObservableProperty]
    public partial string Site { get; set; } = "example.com";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckText))]
    public partial bool IsChecking { get; private set; }

    public string CheckText => Loc.Instance[IsChecking ? "path.checking" : "path.check"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LookupText))]
    public partial bool IsLookingUp { get; private set; }

    public string LookupText => Loc.Instance[IsLookingUp ? "path.lookingUp" : "path.lookup"];

    /// <summary>Почему проверка не началась или не удалась; все в порядке — пусто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Каким путем идет трафик при нынешней защите и что из этого меряют проверки.</summary>
    public string Note => _protection.Status switch
    {
        { IsPaused: true } or { State: not ConnectionState.Connected } => Loc.Instance["path.note.off"],
        { Mode: ConnectionMode.Proxy } => Loc.Instance["path.note.proxy"],
        _ => Loc.Instance["path.note.vpn"],
    };

    /// <summary>Сколько звеньев и участков измерено: «Измерено 7 из 11».</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    /// <summary>Сеть машины заново — при открытии страницы.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _snapshot = await _gateway.InspectNetworkAsync().ConfigureAwait(true) ?? _snapshot;
        Show();
    }

    /// <summary>Сайт по-настоящему: рукопожатие TLS на 443 и трассировка — участки до сайта становятся измеренными.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking)
        {
            return;
        }

        if (ProbeTarget.Normalize(Site) is not { } target)
        {
            Error = Loc.Instance["probe.error.target"];
            return;
        }

        Error = string.Empty;
        IsChecking = true;
        try
        {
            ProbeStep? check = null;
            await foreach (var step in _gateway.ProbeAsync(ProbeKind.Tls, target, 443).ConfigureAwait(true))
            {
                check = step;
            }

            var trace = new List<ProbeStep>();
            await foreach (var step in _gateway.ProbeAsync(ProbeKind.Trace, target, 0).ConfigureAwait(true))
            {
                trace.Add(step);
            }

            (_checkedSite, _siteCheck, _trace) = (target, check, trace);
            if (check is null)
            {
                Error = Loc.Instance["probe.error.service"];
            }

            Show();
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Провайдер реального адреса и владелец адреса выхода — RIPEstat через службу.</summary>
    [RelayCommand]
    private async Task LookupAsync()
    {
        if (IsLookingUp)
        {
            return;
        }

        IsLookingUp = true;
        try
        {
            var exit = _protection.Status.Exit;
            if (IPAddress.TryParse(exit.DirectIp, out var real))
            {
                _provider = await _gateway.DescribeAddressAsync(real).ConfigureAwait(true) ?? _provider;
            }

            if (IPAddress.TryParse(exit.Ip, out var seen))
            {
                _exit = await _gateway.DescribeAddressAsync(seen).ConfigureAwait(true) ?? _exit;
            }

            Show();
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show()
    {
        var site = ProbeTarget.Normalize(Site);
        var checkedNow = site is not null && site == _checkedSite;
        var path = PathBuilder.Build(new PathFacts(_snapshot, _protection.Status, _provider, _exit, site,
            checkedNow ? _siteCheck : null, checkedNow ? _trace : null));
        var expert = IsExpert;
        Steps.Clear();
        for (var i = 0; i < path.Nodes.Count; i++)
        {
            var node = path.Nodes[i];
            Steps.Add(new PathNodeViewModel(node, expert ? NodeExpert(node, checkedNow) : null));
            if (i < path.Links.Count)
            {
                var link = path.Links[i];
                Steps.Add(new PathLinkViewModel(link, node, path.Tunneled, expert ? LinkExpert(link, checkedNow) : null));
            }
        }

        var measured = path.Nodes.Count(n => n.Evidence == Evidence.Measured) + path.Links.Count(l => l.Evidence == Evidence.Measured);
        Summary = Loc.Instance.Format("path.summary", measured, path.Nodes.Count + path.Links.Count);
    }

    partial void OnSiteChanged(string value) => Show();

    // Эксперту — измеренное о звене: канал адаптера, сеть адреса провайдера и выхода, TLS сайта.
    private string? NodeExpert(PathNode node, bool checkedNow)
    {
        var loc = Loc.Instance;
        return node.Kind switch
        {
            PathNodeKind.Adapter when PathBuilder.Physical(_snapshot) is { } card && AdapterCardViewModel.Link(card) is { Length: > 0 } link => link,
            PathNodeKind.Provider when _provider is { Failure: null, Prefix: { } prefix } => loc.Format("path.expert.prefix", prefix),
            PathNodeKind.Vpn when _exit is { Failure: null, Prefix: { } prefix } => loc.Format("path.expert.prefix", prefix),
            PathNodeKind.Site when checkedNow && _siteCheck?.Tls is { } tls => tls.Alpn is { } alpn ? $"{tls.Protocol} · {alpn}" : tls.Protocol,
            _ => null,
        };
    }

    // Эксперту — узлы трассировки за роутером, если сеть провайдера измерена ею.
    private string? LinkExpert(PathLink link, bool checkedNow)
    {
        if (link is not { From: PathNodeKind.Router, Evidence: Evidence.Measured } || !checkedNow || _trace is null)
        {
            return null;
        }

        var hops = _trace.Where(s => s.Index > 1).Select(s => s.Address?.ToString() ?? "*");
        return Loc.Instance.Format("path.expert.hops", string.Join(" → ", hops));
    }
}

/// <summary>Звено карты: значок, имя, подробность и «измерено» или «условно».</summary>
public sealed class PathNodeViewModel
{
    /// <param name="node">Звено.</param>
    /// <param name="expert">Подробность для эксперта; не эксперт или нечего сказать — null.</param>
    public PathNodeViewModel(PathNode node, string? expert = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var loc = Loc.Instance;
        Kind = node.Kind;
        IsMeasured = node.Evidence == Evidence.Measured;
        EvidenceText = loc[IsMeasured ? "path.measured" : "path.conditional"];
        Icon = node.Kind switch
        {
            PathNodeKind.Device => "Icon.Lab.Device",
            PathNodeKind.Adapter => node.Detail == nameof(AdapterKind.Wifi) ? "Icon.Net.WiFi" : node.Detail == nameof(AdapterKind.Cellular) ? "Icon.Net.Mobile" : "Icon.Net.Ethernet",
            PathNodeKind.Router => "Icon.Lab.Router",
            PathNodeKind.Provider => "Icon.Lab.Provider",
            PathNodeKind.Vpn => "Icon.Shield",
            _ => "Icon.Lab.Site",
        };
        var key = node.Kind.ToString().ToLowerInvariant();
        Title = node.Name ?? loc["path.node." + key];
        var parts = new List<string>();
        if (node.Kind is PathNodeKind.Adapter or PathNodeKind.Site && node.Detail is { } code)
        {
            parts.Add(loc[(node.Kind == PathNodeKind.Adapter ? "network.kind." : "probe.outcome.") + code.ToLowerInvariant()]);
        }
        else if (node.Detail is { } detail)
        {
            parts.Add(detail);
        }

        if (node.Address is { } address)
        {
            parts.Add(address.ToString());
        }

        Subtitle = parts.Count > 0 ? string.Join(" · ", parts) : loc["path.node." + key + ".unknown"];
        Expert = expert;
    }

    public string? Expert { get; }

    public bool HasExpert => Expert is not null;

    public PathNodeKind Kind { get; }

    public string Icon { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public bool IsMeasured { get; }

    public string EvidenceText { get; }
}

/// <summary>Участок карты: что за участок, измерен ли, идет ли в туннеле и сколько занял.</summary>
public sealed class PathLinkViewModel
{
    /// <param name="link">Участок.</param>
    /// <param name="from">Звено, с которого участок начинается: по виду адаптера — радио или кабель.</param>
    /// <param name="tunneled">Трафик машины идет в туннель: внутри компьютера его принимает TUN-адаптер.</param>
    /// <param name="expert">Подробность для эксперта; нет — null.</param>
    public PathLinkViewModel(PathLink link, PathNode from, bool tunneled = false, string? expert = null)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(from);
        var loc = Loc.Instance;
        IsMeasured = link.Evidence == Evidence.Measured;
        IsTunnel = link.Tunnel;
        EvidenceText = loc[IsMeasured ? "path.measured" : "path.conditional"];
        var key = (link.From, link.To) switch
        {
            (PathNodeKind.Device, _) => tunneled ? "path.link.inside.vpn" : "path.link.inside",
            (PathNodeKind.Adapter, _) => from.Detail == nameof(AdapterKind.Wifi) ? "path.link.wifi" : "path.link.lan",
            (PathNodeKind.Router, _) => "path.link.provider",
            (PathNodeKind.Provider, PathNodeKind.Vpn) => "path.link.toServer",
            _ => "path.link.internet",
        };
        var parts = new List<string> { loc[key] };
        if (link.Hops is { } hops)
        {
            parts.Add(loc.Format("path.link.hops", hops));
        }

        if (link.Time is { } time)
        {
            parts.Add(loc.Format("probe.ms", time.TotalMilliseconds.ToString(time.TotalMilliseconds < 100 ? "0.#" : "0", loc.Culture)));
        }

        Text = string.Join(" · ", parts);
        TunnelText = IsTunnel ? loc["path.link.tunnel"] : string.Empty;
        Expert = expert;
    }

    public string? Expert { get; }

    public bool HasExpert => Expert is not null;

    public string Text { get; }

    public bool IsMeasured { get; }

    public bool IsConditional => !IsMeasured;

    public bool IsTunnel { get; }

    public string TunnelText { get; }

    public string EvidenceText { get; }
}
