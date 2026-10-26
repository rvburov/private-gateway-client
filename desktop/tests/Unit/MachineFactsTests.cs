using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Факты машины для объяснений лаборатории (этап 8): адаптер выхода, туннель защиты, DNS, сокеты и адреса — только из
/// сведений службы; чего нет — null, а не типичное значение.
/// </summary>
public sealed class MachineFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress TunDns = IPAddress.Parse("172.19.0.2");

    private static readonly NetworkCard WiFi = new("wlan0", "wlan0", AdapterKind.Wifi, true, "00:00:5e:00:53:01", 1500, 866_000_000,
        [new(IPAddress.Parse("fe80::1"), 64), new(IPAddress.Parse("192.0.2.23"), 24), new(IPAddress.Parse("2001:db8:1::23"), 64)], [Router], [Router], null);

    private static readonly NetworkCard Tun = new("pg-tun", string.Empty, AdapterKind.Tunnel, true, null, 1420, null,
        [new(IPAddress.Parse("172.19.0.1"), 30)], [], [TunDns], null);

    private static readonly NetworkCard OtherVpn = new("wg0", string.Empty, AdapterKind.Tunnel, true, null, 1420, null,
        [new(IPAddress.Parse("10.8.0.2"), 24)], [], [IPAddress.Parse("10.8.0.1")], null);

    private static readonly NetworkRoute[] Direct = [new("default", Router, "wlan0", 600), new("10.8.0.0/24", null, "wg0", 0)];

    private static readonly NetworkRoute[] Tunneled = [.. Direct, new("0.0.0.0/1", null, "pg-tun", 0), new("128.0.0.0/1", null, "pg-tun", 0)];

    private static readonly ConnectionStatus Off = ConnectionStatus.Initial(At) with { Exit = new ExitInfo(null, null, null, null, "198.51.100.7") };

    private static readonly ConnectionStatus Vpn = Off with
    {
        State = ConnectionState.Connected,
        Exit = new ExitInfo("203.0.113.24", null, null, null, "198.51.100.7"),
    };

    private static readonly NetworkConnection[] Sockets =
    [
        Socket(SocketProtocol.Tcp, SocketState.Established),
        Socket(SocketProtocol.Tcp, SocketState.Established),
        Socket(SocketProtocol.Tcp, SocketState.Listen),
        Socket(SocketProtocol.Tcp, SocketState.Closing),
        Socket(SocketProtocol.Udp, SocketState.Open),
    ];

    [Fact]
    public void Without_protection_facts_come_from_the_outgoing_adapter()
    {
        var facts = MachineFacts.From(new NetworkSnapshot(At, [OtherVpn, WiFi, Tun with { IsUp = false }], Direct), Off, Sockets);

        facts.Physical.ShouldBe(WiFi);
        facts.Tunnel.ShouldBeNull("чужой туннель без маршрута в интернет и выключенный свой — не туннель защиты");
        facts.TunnelRoutes.ShouldBeEmpty();
        facts.RouteCount.ShouldBe(2);
        facts.Dns.ShouldBe([Router]);
        (facts.TcpOpen, facts.TcpListening, facts.Udp).ShouldBe((2, 1, 1), "закрывающееся соединение — не установленное");
        facts.SeenIp.ShouldBe("198.51.100.7", "без защиты сайты видят реальный адрес");
        facts.RealIp.ShouldBe("198.51.100.7");
    }

    [Fact]
    public void Under_vpn_the_tunnel_carries_internet_routes_and_dns()
    {
        var facts = MachineFacts.From(new NetworkSnapshot(At, [WiFi, OtherVpn, Tun], Tunneled), Vpn, Sockets);

        facts.Physical.ShouldBe(WiFi, "адаптер выхода — по-прежнему Wi-Fi");
        facts.Tunnel.ShouldBe(Tun);
        facts.TunnelRoutes.ShouldBe(["0.0.0.0/1", "128.0.0.0/1"]);
        facts.Dns.ShouldBe([TunDns], "ОС спрашивает DNS через туннель");
        facts.SeenIp.ShouldBe("203.0.113.24", "под защитой сайты видят адрес выхода");
        facts.RealIp.ShouldBe("198.51.100.7");

        MachineFacts.From(new NetworkSnapshot(At, [WiFi, Tun with { DnsServers = [] }], Tunneled), Vpn, null).Dns
            .ShouldBe([Router], "у туннеля нет DNS — остается DNS адаптера");
        MachineFacts.From(new NetworkSnapshot(At, [WiFi, Tun with { IsUp = false }], Tunneled), Vpn, null).Tunnel
            .ShouldBeNull("выключенный туннель с оставшимися маршрутами трафик не везет");
    }

    [Fact]
    public void Paused_protection_reports_the_real_address_as_seen()
    {
        var paused = Vpn with { CaptivePauseUntil = At.AddMinutes(5) };

        MachineFacts.From(null, paused, null).SeenIp.ShouldBe("198.51.100.7");
        MachineFacts.From(null, Off with { Exit = new ExitInfo("198.51.100.9", null, null, null, null) }, null).SeenIp
            .ShouldBe("198.51.100.9", "реального адреса отдельно нет — тот, что видят сайты");
    }

    [Fact]
    public void Without_service_data_nothing_is_made_up()
    {
        var facts = MachineFacts.From(null, ConnectionStatus.Initial(At), null);

        facts.Physical.ShouldBeNull();
        facts.Tunnel.ShouldBeNull();
        facts.RouteCount.ShouldBeNull();
        facts.Dns.ShouldBeEmpty();
        (facts.TcpOpen, facts.TcpListening, facts.Udp).ShouldBe((null, null, null));
        facts.SeenIp.ShouldBeNull();
        facts.RealIp.ShouldBeNull();
    }

    [Fact]
    public void Addresses_skip_link_local_ipv6()
    {
        MachineFacts.Ipv4(WiFi)!.ToString().ShouldBe("192.0.2.23/24");
        MachineFacts.Ipv6(WiFi)!.ToString().ShouldBe("2001:db8:1::23/64", "адрес канала fe80:: — не адрес в интернете");
        MachineFacts.Ipv6(Tun).ShouldBeNull();
        MachineFacts.Ipv4(null).ShouldBeNull();
    }

    private static NetworkConnection Socket(SocketProtocol protocol, SocketState state) =>
        new(new SocketEntry(protocol, new IPEndPoint(IPAddress.Parse("192.0.2.23"), 50000), null, state, null), null);
}
