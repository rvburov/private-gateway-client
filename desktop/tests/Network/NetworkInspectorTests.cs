using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Сетевой инспектор: разбор маршрутов и аренды DHCP каждой ОС на образцах вывода, вид адаптера и MAC, снимок машины через
/// IPC от настоящей службы и перевод снимка в контракт и обратно. Инспектор только читает — ни один тест ничего не меняет.
/// </summary>
public sealed class NetworkInspectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Linux_routes_and_lease_come_from_ip_json()
    {
        var routes = IpNetworkDetails.ParseRoutes("""
            [{"dst":"default","gateway":"192.0.2.1","dev":"wlp2s0","protocol":"dhcp","prefsrc":"192.0.2.23","metric":600,"flags":[]},
             {"dst":"192.0.2.0/24","dev":"wlp2s0","protocol":"kernel","scope":"link","prefsrc":"192.0.2.23","metric":600,"flags":[]},
             {"type":"blackhole","dst":"198.51.100.0/24","flags":[]},
             {"type":"unreachable","dst":"2001:db8:dead::/48","dev":"lo","metric":1024,"flags":[]},
             {"dst":"203.0.113.0/24","flags":[],"nexthops":[{"gateway":"192.0.2.2","dev":"eth0"},{"gateway":"192.0.2.3","dev":"eth1"}]}]
            """).ToList();

        routes.ShouldBe([
            new NetworkRoute("default", IPAddress.Parse("192.0.2.1"), "wlp2s0", 600),
            new NetworkRoute("192.0.2.0/24", null, "wlp2s0", 600),
            new NetworkRoute("203.0.113.0/24", IPAddress.Parse("192.0.2.2"), "eth0", null),
            new NetworkRoute("203.0.113.0/24", IPAddress.Parse("192.0.2.3"), "eth1", null),
        ], "блокирующий и «недоступно» — не путь трафика; у маршрута через два шлюза — две строки");

        IpNetworkDetails.ParseLease("""
            [{"ifname":"wlp2s0","addr_info":[{"family":"inet","local":"192.0.2.23","prefixlen":24,"dynamic":true,"valid_life_time":86213,"preferred_life_time":86213}]}]
            """).ShouldBe(new DhcpLease(null, TimeSpan.FromSeconds(86213)));
        IpNetworkDetails.ParseLease("""
            [{"ifname":"eth0","addr_info":[{"family":"inet","local":"192.0.2.10","prefixlen":24,"valid_life_time":4294967295,"preferred_life_time":4294967295}]}]
            """).ShouldBeNull("адрес вручную — без флага dynamic, не DHCP");
        IpNetworkDetails.ParseLease("""
            [{"ifname":"eth0","addr_info":[{"family":"inet","local":"192.0.2.10","prefixlen":24,"dynamic":true,"valid_life_time":4294967295}]}]
            """).ShouldBe(new DhcpLease(null, null), "получен автоматически, а срок «навсегда» — остаток неизвестен, а не 136 лет");
    }

    [Fact]
    public void Macos_routes_and_lease_come_from_netstat_and_ipconfig()
    {
        var routes = MacNetworkDetails.ParseRoutes("""
            Routing tables

            Internet:
            Destination        Gateway            Flags               Netif Expire
            default            192.0.2.1          UGScg                 en0
            127                127.0.0.1          UCS                   lo0
            192.0.2            link#6             UCS                   en0      !

            """).ToList();
        routes.ShouldBe([
            new NetworkRoute("default", IPAddress.Parse("192.0.2.1"), "en0", null),
            new NetworkRoute("127", IPAddress.Loopback, "lo0", null),
            new NetworkRoute("192.0.2", null, "en0", null),
        ], "link#N — сеть рядом, без шлюза; заголовок и пустые строки — мимо");
        MacNetworkDetails.ParseRoutes("Destination Gateway Flags Netif Expire\r\ndefault fe80::1%1 UGcg en0\r\n").Single().Gateway
            .ShouldNotBeNull().ToString().ShouldBe("fe80::1", "зона адреса — не часть шлюза, конец строки CRLF — тоже");

        MacNetworkDetails.ParseLease("""
            op = BOOTREPLY
            yiaddr = 192.0.2.23
            Options count is 8
            dhcp_message_type (uint8): ACK 0x5
            server_identifier (ip): 192.0.2.1
            lease_time (uint32): 0x15180
            """).ShouldBe(new DhcpLease(IPAddress.Parse("192.0.2.1"), TimeSpan.FromDays(1)));
        MacNetworkDetails.ParseLease(string.Empty).ShouldBeNull();
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Loopback, "lo", "", AdapterKind.Loopback)]
    [InlineData(NetworkInterfaceType.Unknown, "lo0", "", AdapterKind.Loopback)]
    [InlineData(NetworkInterfaceType.Ethernet, "eth0", "", AdapterKind.Ethernet)]
    [InlineData(NetworkInterfaceType.Ethernet, "wlp2s0", "", AdapterKind.Wifi)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Wi-Fi", "Intel(R) Wi-Fi 6", AdapterKind.Wifi)]
    [InlineData(NetworkInterfaceType.Unknown, "utun225", "", AdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Ethernet, "wg0", "", AdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 2", "TAP-Windows Adapter V9", AdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Ppp, "ppp0", "", AdapterKind.Tunnel)]
    [InlineData(NetworkInterfaceType.Wwanpp, "Cellular", "", AdapterKind.Cellular)]
    [InlineData(NetworkInterfaceType.Unknown, "pdp_ip0", "", AdapterKind.Cellular)]
    [InlineData(NetworkInterfaceType.Unknown, "awdl0", "", AdapterKind.Other)]
    public void Kind_of_an_adapter_is_told_by_its_type_and_where_the_type_is_silent_by_its_name(
        NetworkInterfaceType type, string name, string description, AdapterKind kind) =>
        NetworkInspector.Kind(type, name, description).ShouldBe(kind);

    [Fact]
    public void Empty_gateway_is_no_gateway() =>
        NetworkInspector.Gateways([IPAddress.Any, IPAddress.Parse("192.0.2.1"), IPAddress.IPv6Any, IPAddress.Parse("fe80::1")])
            .ShouldBe([IPAddress.Parse("192.0.2.1"), IPAddress.Parse("fe80::1")]);

    [Fact]
    public void Mac_is_written_with_colons_and_missing_one_is_null()
    {
        NetworkInspector.Mac([0x00, 0x00, 0x5e, 0x00, 0x53, 0x01]).ShouldBe("00:00:5e:00:53:01");
        NetworkInspector.Mac([]).ShouldBeNull();
        NetworkInspector.Mac([0, 0, 0, 0, 0, 0]).ShouldBeNull("нули — у туннелей и петли MAC нет");
    }

    [Fact]
    public void Snapshot_goes_through_the_contract_and_comes_back_the_same()
    {
        var snapshot = new NetworkSnapshot(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
        [
            new NetworkCard("Wi-Fi", "Intel(R) Wi-Fi 6", AdapterKind.Wifi, true, "00:00:5e:00:53:01", 1500, 866_000_000,
                [new(IPAddress.Parse("192.0.2.23"), 24), new(IPAddress.Parse("2001:db8::23"), 64)], [IPAddress.Parse("192.0.2.1")],
                [IPAddress.Parse("192.0.2.1"), IPAddress.Parse("2001:db8::1")], new DhcpLease(IPAddress.Parse("192.0.2.1"), TimeSpan.FromHours(20))),
            new NetworkCard("utun225", string.Empty, AdapterKind.Tunnel, false, null, null, null, [], [], [], null),
            new NetworkCard("eth0", string.Empty, AdapterKind.Ethernet, true, "00:00:5e:00:53:02", 1500, null, [], [], [], new DhcpLease(null, null)),
        ],
        [new NetworkRoute("default", IPAddress.Parse("192.0.2.1"), "Wi-Fi", 25), new NetworkRoute("192.0.2.0/24", null, "Wi-Fi", null)]);

        var back = snapshot.ToContract().ToCore();

        back.TakenAt.ShouldBe(snapshot.TakenAt);
        back.Routes.ShouldBe(snapshot.Routes);
        back.Cards.Select(Flat).ShouldBe(snapshot.Cards.Select(Flat));

        foreach (var kind in Enum.GetValues<AdapterKind>())
        {
            kind.ToContract().ToCore().ShouldBe(kind);
        }

        Should.Throw<ArgumentOutOfRangeException>(() => V1.AdapterKind.Unspecified.ToCore());
    }

    [Fact]
    public async Task Service_shows_the_network_of_this_machine_through_ipc()
    {
        await using var service = await RunningService.StartAsync(options: o => o with { NetworkDetails = ServiceHost.NetworkDetails() });

        var snapshot = (await service.Client.InspectNetworkAsync(new V1.InspectNetworkRequest(), cancellationToken: Ct)).ToCore();

        snapshot.TakenAt.ShouldBeInRange(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
        snapshot.Cards.ShouldContain(c => c.Kind == AdapterKind.Loopback && c.Addresses.Any(a => IPAddress.IsLoopback(a.Address)), "петля есть у каждой машины");
        snapshot.Cards.ShouldContain(c => c.IsUp && c.Kind != AdapterKind.Loopback && c.Addresses.Count > 0, "у машины CI есть сеть");
        snapshot.Routes.ShouldContain(r => r.Destination == "default" && r.Gateway != null, "маршрут по умолчанию через шлюз");
        snapshot.Cards.Where(c => c.Kind is AdapterKind.Loopback or AdapterKind.Tunnel).ShouldAllBe(c => c.Mac == null);
    }

    [Fact]
    public async Task Window_client_gets_the_snapshot_from_the_service()
    {
        await using var service = await RunningService.StartAsync(options: o => o with { NetworkDetails = new FakeDetails() });
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);

        var snapshot = await client.InspectNetworkAsync(Ct);

        snapshot.ShouldNotBeNull().Routes.ShouldBe([new NetworkRoute("198.51.100.0/24", IPAddress.Parse("192.0.2.1"), "eth9", 7)], "маршруты — от подробностей ОС");
    }

    // Карточка без списков-коллекций: записи сравниваются по значениям, а списки — по содержимому.
    private static object Flat(NetworkCard c) => (c with { Addresses = [], Gateways = [], DnsServers = [] },
        string.Join(' ', c.Addresses), string.Join(' ', c.Gateways), string.Join(' ', c.DnsServers));

    private sealed class FakeDetails : INetworkDetails
    {
        public IReadOnlyList<NetworkRoute> Routes() => [new NetworkRoute("198.51.100.0/24", IPAddress.Parse("192.0.2.1"), "eth9", 7)];

        public DhcpLease? Dhcp(string adapter) => null;
    }
}
