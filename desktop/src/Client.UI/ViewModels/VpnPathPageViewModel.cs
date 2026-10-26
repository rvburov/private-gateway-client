using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Как меняется путь при VPN» — сценарий лаборатории (этап 8, экрана в макете нет — по дизайн-системе): карта пути без
/// защиты и под ней и что именно меняется — адрес для сайтов, DNS, маршрут, что видит провайдер. «Без защиты» — из снимка
/// службы при включении защиты (<c>CompareNetwork</c>) или нынешней сети; «под защитой» при выключенной защите — модель:
/// ее участки и строки помечены «условно».
/// </summary>
public sealed partial class VpnPathPageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly INavigator _navigator;
    private NetworkSnapshot? _snapshot;
    private NetworkComparison? _comparison;

    public VpnPathPageViewModel(IGatewayClient gateway, ProtectionViewModel protection, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        _gateway = gateway;
        _protection = protection;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) => Show();
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                Show();
            }
        };
        Show();
    }

    /// <summary>Путь без защиты.</summary>
    public PathMapViewModel Before { get; } = new();

    /// <summary>Путь под защитой: настоящий, если защита включена, иначе — модель.</summary>
    public PathMapViewModel After { get; } = new();

    /// <summary>Пояснение ко второй карте: модель или режим прокси; настоящий туннель — пусто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAfterNote))]
    public partial string AfterNote { get; private set; } = string.Empty;

    public bool HasAfterNote => AfterNote.Length > 0;

    /// <summary>Вторая карта — модель: защита выключена.</summary>
    [ObservableProperty]
    public partial bool IsModel { get; private set; }

    /// <summary>Что меняется: строка — до, после и измерено ли.</summary>
    public ObservableCollection<VpnChangeViewModel> Changes { get; } = [];

    /// <summary>Сеть машины и снимок «до» — заново, при открытии страницы.</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        _snapshot = await _gateway.InspectNetworkAsync().ConfigureAwait(true) ?? _snapshot;
        _comparison = await _gateway.CompareNetworkAsync().ConfigureAwait(true) ?? _comparison;
        Show();
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show()
    {
        var loc = Loc.Instance;
        var status = _protection.Status;
        var protectedNow = status is { State: ConnectionState.Connected, IsPaused: false };
        var before = _comparison?.Before;
        var real = before?.Ipv4 ?? status.Exit.DirectIp;

        Before.Show(PathBuilder.Build(new PathFacts(_snapshot, ConnectionStatus.Initial(status.Since) with
        {
            Exit = new ExitInfo(null, null, null, null, real),
        })));

        IsModel = !protectedNow;
        var proxy = protectedNow && status.Mode == ConnectionMode.Proxy;
        AfterNote = IsModel ? loc["vpnpath.after.model"] : proxy ? loc["vpnpath.after.proxy"] : string.Empty;
        if (protectedNow)
        {
            After.Show(PathBuilder.Build(new PathFacts(_snapshot, status)));
        }
        else
        {
            // Модель: туннеля нет — его участки и сервер не измерены, как бы ни были измерены провод и роутер.
            var model = PathBuilder.Build(new PathFacts(_snapshot, status with
            {
                State = ConnectionState.Connected,
                Mode = ConnectionMode.Vpn,
                CaptivePauseUntil = null,
                StrictBlock = false,
                Exit = new ExitInfo(null, null, null, null, real),
            }));
            After.Show(model with { Links = [.. model.Links.Select(l => l.Tunnel ? l with { Evidence = Evidence.Conditional } : l)] });
        }

        var facts = MachineFacts.From(_snapshot, status, null);
        var card = facts.Physical;
        var physicalRoute = before?.Route is { } adapter
            ? Route(adapter, before.Gateway)
            : card is null ? null : Route(card.Name, card.Gateways.Count > 0 ? card.Gateways[0] : null);
        var physicalDns = before is { Dns.Count: > 0 } ? Join(before.Dns) : card is { DnsServers.Count: > 0 } ? Join(card.DnsServers) : null;

        Changes.Clear();
        Changes.Add(new VpnChangeViewModel(loc["vpnpath.change.ip"], real,
            protectedNow ? status.Exit.Ip : null, loc["vpnpath.value.serverIp"]));
        Changes.Add(proxy
            ? new VpnChangeViewModel(loc["vpnpath.change.dns"], physicalDns, physicalDns, null)
            : new VpnChangeViewModel(loc["vpnpath.change.dns"], physicalDns,
                protectedNow && facts.Tunnel is { DnsServers.Count: > 0 } tunnel ? Join(tunnel.DnsServers) : null, loc["vpnpath.value.tunnelDns"]));
        Changes.Add(proxy
            ? new VpnChangeViewModel(loc["vpnpath.change.route"], physicalRoute, physicalRoute, null)
            : new VpnChangeViewModel(loc["vpnpath.change.route"], physicalRoute,
                protectedNow && facts.TunnelRoutes.Count > 0 ? loc.Format("vpnpath.value.tunnelRoute", string.Join(", ", facts.TunnelRoutes)) : null,
                loc.Format("vpnpath.value.tunnelRoute", "0.0.0.0/1, 128.0.0.0/1")));

        // Что видит провайдер — объяснение, а не замер: строка всегда «условно».
        Changes.Add(new VpnChangeViewModel(loc["vpnpath.change.provider"], null, null, loc[proxy ? "vpnpath.value.proxyStream" : "vpnpath.value.stream"],
            loc["vpnpath.value.sites"]));

        static string Join(IEnumerable<IPAddress> addresses) => string.Join(", ", addresses);

        string Route(string adapter, IPAddress? gateway) => gateway is null ? adapter : loc.Format("compare.route.via", adapter, gateway);
    }
}

/// <summary>
/// Что меняется под защитой: было и стало. Измеренного нет — подставляется модель (<paramref name="modelAfter"/>,
/// <paramref name="modelBefore"/>), и строка — «условно».
/// </summary>
public sealed class VpnChangeViewModel(string label, string? before, string? after, string? modelAfter, string? modelBefore = null)
{
    public string Label { get; } = label;

    public string Before { get; } = before ?? modelBefore ?? Loc.Instance["compare.none"];

    public string After { get; } = after ?? modelAfter ?? Loc.Instance["compare.none"];

    /// <summary>И «было», и «стало» — замеры службы.</summary>
    public bool IsMeasured { get; } = before is not null && after is not null;

    public string EvidenceText => Loc.Instance[IsMeasured ? "path.measured" : "path.conditional"];
}
