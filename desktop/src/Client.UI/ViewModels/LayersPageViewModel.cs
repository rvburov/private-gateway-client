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
/// «Как устроена сеть» — сетевая лаборатория (этап 8, экрана в макете нет — по дизайн-системе): семь уровней OSI, VPN на
/// уровнях и темы IP, TCP и UDP, DNS, TLS, HTTP(S), TUN, маршрутизация. Под каждым объяснением — факты этой машины от
/// службы: сеть, сокеты, состояние защиты, по «Измерить» — DNS и TLS сайта. Чего служба не дала — «не измерено», а не
/// типичное значение. «Эксперт» добавляет подробности и факты (<see cref="Core.Settings.AppSettings.LabExpert"/>).
/// </summary>
public sealed partial class LayersPageViewModel : ObservableObject
{
    // Уровень или тема: ключ текстов и какие факты показать начинающему и сверх того — эксперту.
    private static readonly (string Key, string[] Basic, string[] Expert)[] LayerSpec =
    [
        ("7", ["dns", "seenIp"], ["realIp"]),
        ("6", ["tls"], ["cert"]),
        ("5", ["alpn"], []),
        ("4", ["tcpOpen", "udp"], ["tcpListen"]),
        ("3", ["ipv4", "gateway"], ["ipv6", "routes", "defaultRoute", "tunnelRoutes"]),
        ("2", ["mac"], ["mtu"]),
        ("1", ["adapter", "speed"], []),
    ];

    private static readonly (string Key, string[] Basic, string[] Expert)[] TopicSpec =
    [
        ("ip", ["ipv4", "seenIp"], ["ipv6", "realIp"]),
        ("transport", ["tcpOpen", "udp"], ["tcpListen"]),
        ("dns", ["dns", "resolved"], []),
        ("tls", ["tls", "cert"], []),
        ("http", ["alpn"], []),
        ("tun", ["tunnel"], ["tunnelMtu", "tunnelRoutes"]),
        ("routing", ["defaultRoute", "routes"], ["tunnelRoutes"]),
    ];

    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;
    private NetworkSnapshot? _snapshot;
    private IReadOnlyList<NetworkConnection>? _connections;
    private ProbeStep? _tls;
    private IReadOnlyList<IPAddress>? _resolved;

    public LayersPageViewModel(IGatewayClient gateway, ProtectionViewModel protection, SettingsService settings, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(settings);
        _gateway = gateway;
        _protection = protection;
        _settings = settings;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            Show();
            OnPropertyChanged(nameof(MeasureText));
        };
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                Show();
            }
        };
        settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsExpert));
            OnPropertyChanged(nameof(IsBeginner));
            Show();
        };
        Show();
    }

    /// <summary>Семь уровней — сверху прикладной, снизу физический, как их рисуют.</summary>
    public ObservableCollection<LabCardViewModel> Layers { get; } = [];

    /// <summary>Протоколы и устройство: IP, TCP и UDP, DNS, TLS, HTTP(S), TUN, маршрутизация.</summary>
    public ObservableCollection<LabCardViewModel> Topics { get; } = [];

    /// <summary>VPN на уровнях: пакет в пакете — и что сейчас на этой машине.</summary>
    [ObservableProperty]
    public partial LabCardViewModel? Tunnel { get; private set; }

    /// <summary>Объяснения для эксперта: подробности, заголовки, диапазоны и больше фактов.</summary>
    public bool IsExpert
    {
        get => _settings.Current.LabExpert;
        set => _settings.Update(s => s with { LabExpert = value });
    }

    /// <summary>Для начинающего — обратная сторона переключателя (две кнопки сегмента).</summary>
    public bool IsBeginner
    {
        get => !IsExpert;
        set => IsExpert = !value;
    }

    /// <summary>Сайт для замера DNS и TLS.</summary>
    [ObservableProperty]
    public partial string Site { get; set; } = "example.com";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MeasureText))]
    public partial bool IsMeasuring { get; private set; }

    public string MeasureText => Loc.Instance[IsMeasuring ? "layers.measuring" : "layers.measure"];

    /// <summary>Почему замер не начался или не удался; все в порядке — пусто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Сеть и сокеты машины заново — при открытии страницы.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _snapshot = await _gateway.InspectNetworkAsync().ConfigureAwait(true) ?? _snapshot;
        _connections = await _gateway.ListConnectionsAsync().ConfigureAwait(true) ?? _connections;
        Show();
    }

    /// <summary>DNS и TLS сайта по-настоящему: имя — в адреса, рукопожатие на 443 — версия, шифр, протокол и сертификат.</summary>
    [RelayCommand]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            return;
        }

        if (ProbeTarget.Normalize(Site) is not { } target)
        {
            Error = Loc.Instance["probe.error.target"];
            return;
        }

        Error = string.Empty;
        IsMeasuring = true;
        try
        {
            var resolved = new List<IPAddress>();
            var answered = false;
            await foreach (var step in _gateway.ProbeAsync(ProbeKind.Dns, target, 0).ConfigureAwait(true))
            {
                answered = true;
                if (step is { Outcome: ProbeOutcome.Ok, Address: { } address })
                {
                    resolved.Add(address);
                }
            }

            ProbeStep? tls = null;
            await foreach (var step in _gateway.ProbeAsync(ProbeKind.Tls, target, 443).ConfigureAwait(true))
            {
                tls = step;
            }

            (_resolved, _tls) = (answered ? resolved : null, tls);
            if (!answered && tls is null)
            {
                Error = Loc.Instance["probe.error.service"];
            }

            Show();
        }
        finally
        {
            IsMeasuring = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show()
    {
        var loc = Loc.Instance;
        var facts = MachineFacts.From(_snapshot, _protection.Status, _connections);
        var expert = IsExpert;

        Layers.Clear();
        foreach (var (key, basic, more) in LayerSpec)
        {
            Layers.Add(new LabCardViewModel(key, loc[$"layer.{key}.name"], loc[$"layer.{key}.text"], expert ? loc[$"layer.{key}.expert"] : null,
                loc[$"layer.{key}.protocols"], Facts(facts, expert ? [.. basic, .. more] : basic)));
        }

        Topics.Clear();
        foreach (var (key, basic, more) in TopicSpec)
        {
            Topics.Add(new LabCardViewModel(null, loc[$"topic.{key}.title"], loc[$"topic.{key}.text"], expert ? loc[$"topic.{key}.expert"] : null,
                null, Facts(facts, expert ? [.. basic, .. more] : basic)));
        }

        var status = _protection.Status;
        var now = status switch
        {
            { IsPaused: true } or { State: not ConnectionState.Connected } => "layers.tunnel.off",
            { Mode: ConnectionMode.Proxy } => "layers.tunnel.proxy",
            _ => "layers.tunnel.on",
        };
        Tunnel = new LabCardViewModel(null, loc["layers.tunnel.title"], loc["layers.tunnel.text"], expert ? loc["layers.tunnel.expert"] : null,
            null, [new FactRowViewModel(loc["fact.now"], loc[now], true)]);
    }

    private List<FactRowViewModel> Facts(MachineFacts facts, IEnumerable<string> keys) =>
        [.. keys.Select(k => Fact(facts, k))];

    /// <summary>Факт по ключу: значение и измерено ли оно; служба не дала — «не измерено».</summary>
    private FactRowViewModel Fact(MachineFacts facts, string key)
    {
        var loc = Loc.Instance;
        var snapshot = _snapshot is not null;
        var card = facts.Physical;
        var tls = _tls?.Tls;
        string? value = key switch
        {
            "dns" when snapshot => facts.Dns.Count == 0 ? loc["lab.none"]
                : facts.Tunnel is { DnsServers.Count: > 0 } ? loc.Format("fact.dns.tunnel", Join(facts.Dns)) : Join(facts.Dns),
            "seenIp" => facts.SeenIp,
            "realIp" => facts.RealIp,
            "tls" => tls is not null ? loc.Format("fact.tls.value", tls.Protocol, tls.Cipher)
                : _tls is { } failed ? loc["probe.outcome." + failed.Outcome.ToString().ToLowerInvariant()] : null,
            "cert" => tls is null ? null
                : tls.Problems.Count == 0 ? loc["probe.tls.ok"]
                : string.Join(", ", tls.Problems.Select(p => loc["probe.tls.problem." + p.ToString().ToLowerInvariant()])),
            "alpn" => tls is null ? null : tls.Alpn ?? loc["lab.none"],
            "resolved" => _resolved is null ? null : _resolved.Count == 0 ? loc["probe.outcome.notfound"] : Join(_resolved),
            "tcpOpen" => Count(facts.TcpOpen),
            "tcpListen" => Count(facts.TcpListening),
            "udp" => Count(facts.Udp),
            "ipv4" when snapshot => MachineFacts.Ipv4(card)?.ToString() ?? loc["lab.none"],
            "ipv6" when snapshot => MachineFacts.Ipv6(card)?.ToString() ?? loc["lab.none"],
            "gateway" when snapshot => card is { Gateways.Count: > 0 } ? card.Gateways[0].ToString() : loc["lab.none"],
            "routes" => Count(facts.RouteCount),
            "defaultRoute" when snapshot => _snapshot!.Routes.FirstOrDefault(r => r.Destination == "default") is { } route
                ? route.Gateway is { } via ? loc.Format("fact.defaultRoute.value", route.Interface, via) : route.Interface
                : loc["lab.none"],
            "tunnelRoutes" when snapshot => facts.TunnelRoutes.Count == 0 ? loc["lab.none"] : string.Join(", ", facts.TunnelRoutes),
            "mac" when snapshot => card?.Mac ?? loc["lab.none"],
            "mtu" when snapshot => card?.Mtu is { } mtu ? loc.Format("fact.mtu.value", mtu) : loc["lab.none"],
            "adapter" when snapshot => card is null ? loc["lab.none"] : $"{card.Name} · {loc["network.kind." + card.Kind.ToString().ToLowerInvariant()]}",
            "speed" when snapshot => card?.SpeedBitsPerSecond is { } speed ? AdapterCardViewModel.Speed(speed) : loc["lab.none"],
            "tunnel" when snapshot => facts.Tunnel is { } tunnel
                ? MachineFacts.Ipv4(tunnel) is { } address ? $"{tunnel.Name} · {address}" : tunnel.Name
                : loc["fact.tunnel.none"],
            "tunnelMtu" when snapshot => facts.Tunnel?.Mtu is { } mtu ? loc.Format("fact.mtu.value", mtu) : loc["lab.none"],
            _ => null,
        };
        return new FactRowViewModel(loc["fact." + key], value ?? loc["lab.notMeasured"], value is not null);

        static string Join(IEnumerable<IPAddress> addresses) => string.Join(", ", addresses);

        static string? Count(int? count) => count?.ToString(CultureInfo.CurrentCulture);
    }
}

/// <summary>Карточка лаборатории: номер уровня (у темы — нет), объяснение, подробности для эксперта, протоколы и факты.</summary>
public sealed class LabCardViewModel(string? badge, string title, string text, string? expert, string? protocols, IReadOnlyList<FactRowViewModel> facts)
{
    public string? Badge { get; } = badge;

    public bool HasBadge => Badge is not null;

    public string Title { get; } = title;

    public string Text { get; } = text;

    public string? Expert { get; } = expert;

    public bool HasExpert => Expert is not null;

    public string? Protocols { get; } = protocols is null ? null : Loc.Instance["layers.protocols.label"] + ": " + protocols;

    public bool HasProtocols => Protocols is not null;

    public IReadOnlyList<FactRowViewModel> Facts { get; } = facts;
}

/// <summary>Факт машины: что, значение и измерено ли (иначе — «не измерено» приглушенно).</summary>
public sealed record FactRowViewModel(string Label, string Value, bool IsMeasured);
