using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Путь до сайта (лаборатория, этап 8): звено или участок «измерено» только по сведениям ОС или замеру, и замер засчитывается
/// только тому участку, по которому шел; остальное — «условно».
/// </summary>
public sealed class PathBuilderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");

    private static readonly NetworkCard WiFi = Card("wlan0", AdapterKind.Wifi, "192.0.2.10", Router);

    private static readonly NetworkSnapshot Snapshot = new(At,
        [
            Card("lo", AdapterKind.Loopback, "127.0.0.1"),
            Card("eth0", AdapterKind.Ethernet, "198.51.100.10", IPAddress.Parse("198.51.100.1"), up: false),
            WiFi,
            Card("pg-tun", AdapterKind.Tunnel, "172.19.0.1", IPAddress.Parse("172.19.0.2")),
        ],
        [new NetworkRoute("default", Router, "wlan0", 600), new NetworkRoute("192.0.2.0/24", null, "wlan0", 600)]);

    private static readonly ConnectionStatus Off = ConnectionStatus.Initial(At) with { Exit = new ExitInfo(null, null, null, null, "198.51.100.7") };

    private static readonly ConnectionStatus Vpn = Off with
    {
        State = ConnectionState.Connected,
        Mode = ConnectionMode.Vpn,
        Exit = new ExitInfo("203.0.113.24", "Амстердам", null, null, "198.51.100.7"),
    };

    private static readonly ProbeStep SiteOk = new(1, IPAddress.Parse("203.0.113.80"), TimeSpan.FromMilliseconds(48), ProbeOutcome.Ok);

    private static readonly ProbeStep[] Trace =
    [
        new(1, Router, TimeSpan.FromMilliseconds(2.4), ProbeOutcome.Hop),
        new(2, null, null, ProbeOutcome.TimedOut),
        new(3, IPAddress.Parse("198.51.100.1"), TimeSpan.FromMilliseconds(9), ProbeOutcome.Hop),
        new(4, IPAddress.Parse("203.0.113.80"), TimeSpan.FromMilliseconds(47), ProbeOutcome.Ok),
    ];

    [Fact]
    public void Without_protection_the_path_goes_from_the_provider_straight_to_the_site()
    {
        var path = PathBuilder.Build(new PathFacts(Snapshot, Off, Site: "example.com"));

        path.Nodes.Select(n => n.Kind).ShouldBe([PathNodeKind.Device, PathNodeKind.Adapter, PathNodeKind.Router, PathNodeKind.Provider, PathNodeKind.Site]);
        path.Links.Select(l => (l.From, l.To)).ShouldBe([
            (PathNodeKind.Device, PathNodeKind.Adapter), (PathNodeKind.Adapter, PathNodeKind.Router), (PathNodeKind.Router, PathNodeKind.Provider),
            (PathNodeKind.Provider, PathNodeKind.Site),
        ]);
        path.Protected.ShouldBeFalse();
        path.Links.ShouldAllBe(l => !l.Tunnel);

        var adapter = path.Nodes[1];
        (adapter.Name, adapter.Address, adapter.Detail, adapter.Evidence).ShouldBe(("wlan0", IPAddress.Parse("192.0.2.10"), "Wifi", Evidence.Measured),
            "адаптер выхода — Wi-Fi с маршрутом по умолчанию, не выключенный кабель, не петля и не туннель");
        path.Nodes[2].Address.ShouldBe(Router);
        path.Nodes[2].Evidence.ShouldBe(Evidence.Measured, "шлюз назвала ОС");
        path.Nodes[3].Address.ShouldBe(IPAddress.Parse("198.51.100.7"));
        path.Nodes[3].Evidence.ShouldBe(Evidence.Measured, "реальный адрес измерила служба");
        path.Nodes[3].Name.ShouldBeNull("кто провайдер — не спрашивали");

        path.Nodes[4].Evidence.ShouldBe(Evidence.Conditional, "сайт не проверяли");
        path.Links[2].Evidence.ShouldBe(Evidence.Conditional, "сеть провайдера не трассировали");
        path.Links[3].Evidence.ShouldBe(Evidence.Conditional);
    }

    [Fact]
    public void Check_and_trace_without_protection_measure_the_provider_network_and_the_site()
    {
        var provider = new AddressInfo(IPAddress.Parse("198.51.100.7"), 64500, "Example Telecom", "198.51.100.0/24", "NL", null, null);

        var path = PathBuilder.Build(new PathFacts(Snapshot, Off, provider, null, "example.com", SiteOk, Trace));

        path.Nodes[3].Name.ShouldBe("Example Telecom");
        path.Nodes[3].Detail.ShouldBe("AS64500");
        path.Links[1].Time.ShouldBe(TimeSpan.FromMilliseconds(2.4), "первый узел трассировки — роутер");
        path.Links[2].Evidence.ShouldBe(Evidence.Measured, "за роутером ответили узлы провайдера");
        path.Links[2].Hops.ShouldBe(3, "ответившие узлы, без «нет ответа»");
        var site = path.Nodes[4];
        (site.Name, site.Address, site.Detail, site.Evidence).ShouldBe(("example.com", IPAddress.Parse("203.0.113.80"), null, Evidence.Measured));
        path.Links[3].Evidence.ShouldBe(Evidence.Measured);
        path.Links[3].Time.ShouldBe(TimeSpan.FromMilliseconds(48));
    }

    [Fact]
    public void A_trace_where_only_the_router_answered_does_not_measure_the_provider()
    {
        var path = PathBuilder.Build(new PathFacts(Snapshot, Off, Trace: [Trace[0], Trace[1]]));

        path.Links[2].Evidence.ShouldBe(Evidence.Conditional);
        path.Links[2].Hops.ShouldBe(1);

        PathBuilder.Build(new PathFacts(Snapshot, Off, Trace: [Trace[1] with { Index = 1 }, Trace[2] with { Index = 2 }])).Links[1].Time
            .ShouldBeNull("роутер промолчал — время следующего узла не его");
    }

    [Fact]
    public void In_vpn_mode_everything_up_to_the_server_is_in_the_tunnel_and_the_trace_does_not_count()
    {
        var path = PathBuilder.Build(new PathFacts(Snapshot, Vpn, null, null, "example.com", SiteOk, Trace));

        path.Nodes.Select(n => n.Kind).ShouldBe([PathNodeKind.Device, PathNodeKind.Adapter, PathNodeKind.Router, PathNodeKind.Provider, PathNodeKind.Vpn, PathNodeKind.Site]);
        path.Protected.ShouldBeTrue();
        path.Nodes[1].Name.ShouldBe("wlan0", "туннель — звено VPN, а не адаптер выхода");
        var vpn = path.Nodes[4];
        (vpn.Address, vpn.Detail, vpn.Evidence).ShouldBe((IPAddress.Parse("203.0.113.24"), "Амстердам", Evidence.Measured));

        path.Links.Select(l => l.Tunnel).ShouldBe([false, true, true, true, false], "туннель — от адаптера машины до сервера");
        path.Tunneled.ShouldBeTrue();
        path.Links[1].Time.ShouldBeNull("трассировка в туннеле не доходит до роутера");
        path.Links[2].Evidence.ShouldBe(Evidence.Conditional, "трассировка в туннеле сеть провайдера не меряет");
        path.Links[2].Hops.ShouldBeNull();
        path.Links[3].Evidence.ShouldBe(Evidence.Measured, "адрес выхода измерен через сервер");
        path.Links[4].Evidence.ShouldBe(Evidence.Measured, "проверка сайта шла через туннель и сервер");
        path.Links[4].Time.ShouldBe(TimeSpan.FromMilliseconds(48));
    }

    [Fact]
    public void In_proxy_mode_the_direct_site_check_does_not_measure_the_server_to_site_segment()
    {
        var proxy = Vpn with { Mode = ConnectionMode.Proxy };

        var path = PathBuilder.Build(new PathFacts(Snapshot, proxy, null, null, "example.com", SiteOk, Trace));

        path.Links.ShouldAllBe(l => !l.Tunnel, "в режиме прокси туннеля нет — через сервер идут только программы с прокси");
        path.Tunneled.ShouldBeFalse();
        path.Links[2].Evidence.ShouldBe(Evidence.Measured, "служба трассирует напрямую — по проводу");
        path.Nodes[5].Evidence.ShouldBe(Evidence.Measured, "сайт ответил");
        path.Links[4].Evidence.ShouldBe(Evidence.Conditional, "проверка шла мимо сервера");
        path.Links[4].Time.ShouldBeNull();
    }

    [Fact]
    public void Paused_protection_is_drawn_as_off()
    {
        var paused = Vpn with { CaptivePauseUntil = At.AddMinutes(5) };

        var path = PathBuilder.Build(new PathFacts(Snapshot, paused));

        path.Protected.ShouldBeFalse();
        path.Links.ShouldAllBe(l => !l.Tunnel);
    }

    [Fact]
    public void Server_without_a_measured_exit_address_is_conditional()
    {
        var connecting = Vpn with { Exit = new ExitInfo(null, null, null, null, null) };

        var path = PathBuilder.Build(new PathFacts(Snapshot, connecting));

        path.Nodes[4].Evidence.ShouldBe(Evidence.Conditional);
        path.Links[3].Evidence.ShouldBe(Evidence.Conditional);
        path.Nodes[3].Evidence.ShouldBe(Evidence.Conditional, "реальный адрес не измерен и провайдера не спрашивали");
    }

    [Fact]
    public void Failed_lookups_and_checks_stay_conditional_and_say_why()
    {
        var failed = AddressInfo.Failed(IPAddress.Parse("198.51.100.7"), AddressFailure.Unreachable);
        var exitFailed = AddressInfo.Failed(IPAddress.Parse("203.0.113.24"), AddressFailure.Unreachable);
        var refused = new ProbeStep(1, IPAddress.Parse("203.0.113.80"), null, ProbeOutcome.Refused);
        var unknownReal = Off with { Exit = ExitInfo.Unknown };

        var path = PathBuilder.Build(new PathFacts(Snapshot, unknownReal, failed, exitFailed, "example.com", refused));

        path.Nodes[3].Evidence.ShouldBe(Evidence.Conditional, "RIPEstat не ответил, реального адреса нет");
        var site = path.Nodes[4];
        (site.Detail, site.Evidence).ShouldBe(("Refused", Evidence.Conditional));
        path.Links[3].Evidence.ShouldBe(Evidence.Conditional);
    }

    [Fact]
    public void FakeDns_address_is_not_shown_as_the_site_address()
    {
        var fake = SiteOk with { Address = IPAddress.Parse("198.18.0.5") };

        var path = PathBuilder.Build(new PathFacts(Snapshot, Vpn, Site: "example.com", SiteCheck: fake));

        path.Nodes[5].Address.ShouldBeNull("198.18.0.0/15 — подставной адрес ядра");
        path.Nodes[5].Evidence.ShouldBe(Evidence.Measured, "сайт при этом ответил");
        PathBuilder.Build(new PathFacts(Snapshot, Vpn, SiteCheck: SiteOk with { Address = IPAddress.Parse("198.20.0.5") }))
            .Nodes[5].Address.ShouldBe(IPAddress.Parse("198.20.0.5"), "за пределами 198.18.0.0/15 — настоящий адрес");
    }

    [Fact]
    public void Without_the_network_snapshot_the_adapter_and_router_are_conditional()
    {
        var path = PathBuilder.Build(new PathFacts(null, Off));

        path.Nodes[1].Evidence.ShouldBe(Evidence.Conditional);
        path.Nodes[1].Name.ShouldBeNull();
        path.Nodes[2].Evidence.ShouldBe(Evidence.Conditional);
        path.Links[1].Evidence.ShouldBe(Evidence.Conditional);
        path.Links[0].Evidence.ShouldBe(Evidence.Measured, "внутри компьютера путь есть всегда");
    }

    [Fact]
    public void Outgoing_adapter_is_the_one_with_the_default_route_or_else_the_first_with_a_gateway()
    {
        var ethernet = Card("eth1", AdapterKind.Ethernet, "198.51.100.20", IPAddress.Parse("198.51.100.1"));

        PathBuilder.Physical(Snapshot with { Cards = [ethernet, WiFi] })!.Name.ShouldBe("wlan0", "маршрут по умолчанию — через Wi-Fi");
        PathBuilder.Physical(Snapshot with { Cards = [ethernet, WiFi], Routes = [] })!.Name.ShouldBe("eth1", "маршрутов нет — первый со шлюзом");
        PathBuilder.Physical(Snapshot with { Routes = [new NetworkRoute("default", null, "pg-tun", 0)] })!.Name
            .ShouldBe("wlan0", "маршрут по умолчанию через туннель — не адаптер выхода");
        PathBuilder.Physical(Snapshot with { Cards = [Card("wlan0", AdapterKind.Wifi, "192.0.2.10")], Routes = [] }).ShouldBeNull();
        PathBuilder.Physical(null).ShouldBeNull();
    }

    [Fact]
    public void Router_prefers_the_ipv4_gateway()
    {
        var dual = WiFi with { Gateways = [IPAddress.Parse("fe80::1"), Router] };

        PathBuilder.Build(new PathFacts(Snapshot with { Cards = [dual] }, Off)).Nodes[2].Address.ShouldBe(Router);
        PathBuilder.Build(new PathFacts(Snapshot with { Cards = [dual with { Gateways = [IPAddress.Parse("fe80::1")] }] }, Off))
            .Nodes[2].Address.ShouldBe(IPAddress.Parse("fe80::1"), "только IPv6 — и он годится");
    }

    private static NetworkCard Card(string name, AdapterKind kind, string address, IPAddress? gateway = null, bool up = true) =>
        new(name, name, kind, up, null, 1500, null, [new NetworkAddress(IPAddress.Parse(address), 24)], gateway is null ? [] : [gateway], [], null);
}
