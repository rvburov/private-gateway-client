using System.Net.NetworkInformation;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Маршруты машины для поиска чужого VPN: разбор вывода <c>ip -j</c> (Linux), настоящий туннель в своем
/// сетевом пространстве Linux, таблица маршрутов Windows (CI) и признаки туннеля у адаптеров Windows.
/// Правила маршрутизации, оставшиеся после аварии sing-box, — по номерам туннеля.
/// </summary>
[Collection(NamespaceSerial.Name)]
public sealed class RouteTableTests
{
    [Fact]
    public void Ip_links_tell_tunnels_from_cards_and_skip_what_is_off()
    {
        var links = IpRouteTable.Links("""
            [{"ifname":"lo","flags":["LOOPBACK","UP","LOWER_UP"],"link_type":"loopback"},
             {"ifname":"eth0","flags":["BROADCAST","MULTICAST","UP","LOWER_UP"],"link_type":"ether"},
             {"ifname":"wg0","flags":["POINTOPOINT","NOARP","UP","LOWER_UP"],"link_type":"none","linkinfo":{"info_kind":"wireguard"}},
             {"ifname":"tap0","flags":["BROADCAST","UP"],"link_type":"ether","linkinfo":{"info_kind":"tun","info_data":{"type":"tap"}}},
             {"ifname":"ppp0","flags":["POINTOPOINT","UP"],"link_type":"ppp"},
             {"ifname":"he-ipv6","flags":["POINTOPOINT","UP"],"link_type":"sit","linkinfo":{"info_kind":"sit"}},
             {"ifname":"tun9","flags":["POINTOPOINT","NOARP"],"link_type":"none","linkinfo":{"info_kind":"tun"}}]
            """);

        links.ShouldBe(new Dictionary<string, bool>
        {
            ["lo"] = false,
            ["eth0"] = false,
            ["wg0"] = true,
            ["tap0"] = true,
            ["ppp0"] = true,
            ["he-ipv6"] = false,
        }, ignoreOrder: true, "выключенный tun9 не в счет; туннель IPv6 через IPv4 — не VPN");
    }

    [Fact]
    public void Ip_routes_to_the_whole_internet_are_found_in_every_table()
    {
        var devices = IpRouteTable.Devices("""
            [{"dst":"default","gateway":"192.0.2.1","dev":"eth0","flags":[]},
             {"dst":"default","dev":"wg0","table":"51820","scope":"link","flags":[]},
             {"dst":"0.0.0.0/1","gateway":"198.51.100.1","dev":"tun0","flags":[]},
             {"dst":"128.0.0.0/1","gateway":"198.51.100.1","dev":"tun0","flags":[]},
             {"dst":"192.0.2.0/24","dev":"eth0","protocol":"kernel","scope":"link","flags":[]},
             {"type":"unreachable","dst":"default","table":"7","flags":[]},
             {"type":"local","dst":"default","dev":"lo","table":"local","flags":[]},
             {"dst":"default","flags":[],"nexthops":[{"gateway":"192.0.2.1","dev":"eth0"},{"gateway":"203.0.113.1","dev":"usb0"}]},
             {"dst":"8000::/1","dev":"tun6","flags":[]},
             {"dst":"2001:db8::/32","dev":"tun6","flags":[]}]
            """);

        devices.ShouldBe(["eth0", "wg0", "tun0", "tun0", "eth0", "usb0", "tun6"],
            "основной маршрут, своя таблица wg-quick, половины интернета и несколько шлюзов; блокирующие и частные — мимо");
    }

    [Fact]
    public void Tunnel_with_a_route_to_the_whole_internet_is_found_in_a_namespace()
    {
        NetworkNamespace.SkipUnlessAvailable("ip");
        using var netns = NetworkNamespace.Create();
        var routes = new IpRouteTable(arguments => netns.Run(null, ["ip", .. arguments]));
        netns.Run(null, "ip", "tuntap", "add", "dev", "pgother0", "mode", "tun");
        netns.Run(null, "ip", "link", "set", "pgother0", "up");

        routes.DefaultRoutes().ShouldBeEmpty("адаптер без маршрута ко всему интернету — не VPN");

        netns.Run(null, "ip", "route", "add", "default", "dev", "pgother0", "table", "51820");
        netns.Run(null, "ip", "route", "add", "128.0.0.0/1", "dev", "pgother0");

        routes.DefaultRoutes().ShouldBe([new DefaultRoute("pgother0", IsTunnel: true), new DefaultRoute("pgother0", IsTunnel: true)]);
        OtherVpn.Find(routes.DefaultRoutes(), ownAdapter: "pgtun0").ShouldBe(["pgother0"]);

        netns.Run(null, "ip", "link", "set", "pgother0", "down");

        routes.DefaultRoutes().ShouldBeEmpty("выключенный адаптер трафик не уводит");
    }

    [Fact]
    public void Windows_route_table_has_the_default_route_of_the_machine()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("таблица маршрутов Windows");
            return;
        }

        var routes = new WindowsRouteTable().DefaultRoutes();

        routes.ShouldContain(r => !r.IsTunnel, "у машины CI маршрут ко всему интернету — через сетевую карту");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n', routes));
    }

    [Fact]
    public void Netstat_routes_take_whole_internet_routes_and_skip_scoped_ones()
    {
        var inet = NetstatRouteTable.Parse("""
            Routing tables

            Internet:
            Destination        Gateway            Flags               Netif Expire
            default            192.168.1.1        UGScg                 en0
            default            192.168.1.1        UGScIg                en7
            0/1                10.8.0.1           UGSc                utun4
            127                127.0.0.1          UCS                   lo0
            128.0/1            10.8.0.1           UGSc                utun4
            192.168.1          link#6             UCS                   en0      !
            """).ToList();
        inet.ShouldBe([new DefaultRoute("en0", false), new DefaultRoute("utun4", true), new DefaultRoute("utun4", true)],
            "привязанный к адаптеру default (флаг I) трафик машины не забирает");

        var inet6 = NetstatRouteTable.Parse("""
            Internet6:
            Destination                             Gateway                                 Flags               Netif Expire
            default                                 fe80::%utun0                            UGcIg               utun0
            default                                 fe80::%utun1                            UGcIg               utun1
            ::/1                                    fd00::1                                 UGSc                ppp0
            ::1                                     ::1                                     UHL                   lo0
            8000::/1                                fd00::1                                 UGSc                ipsec0
            """).ToList();
        inet6.ShouldBe([new DefaultRoute("ppp0", true), new DefaultRoute("ipsec0", true)], "штатные utun0–utun3 с привязанным default — не чужой VPN");
        OtherVpn.Find(inet.Concat(inet6), "utun225").ShouldBe(["ipsec0", "ppp0", "utun4"]);
        NetstatRouteTable.Parse("default 192.168.1.1 UGSc en0").Single().IsTunnel.ShouldBeFalse();
        NetstatRouteTable.Parse("default 192.168.1.1 UGSc en0\r\n").Single().Adapter.ShouldBe("en0", "конец строки CRLF — не часть имени адаптера");
        NetstatRouteTable.Parse("default 10.0.0.1 UGSc utunnel").Single().IsTunnel.ShouldBeFalse("туннель — utun с номером");
        NetstatRouteTable.Parse("default 192.168.1.1 GSc en0").ShouldBeEmpty("маршрут не поднят (нет U)");
    }

    [Fact]
    public void Macos_route_table_has_the_default_route_of_the_machine()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("таблица маршрутов macOS");
            return;
        }

        var routes = NetstatRouteTable.ForThisMachine()!.DefaultRoutes();

        routes.ShouldContain(r => !r.IsTunnel, "у машины CI маршрут ко всему интернету — через сетевую карту");
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join('\n', routes));
    }

    [Fact]
    public void Rules_left_by_the_tunnel_are_removed_by_their_numbers_only()
    {
        IpRules.Priorities("""
            [{"priority":0,"src":"all","table":"local"},{"priority":9047,"dst":"172.19.0.0/30","table":"20551"},
             {"priority":9049,"src":"all","table":"main","suppress_prefixlen":0},{"priority":9049,"iif":"pgtun0","action":"goto","target":9057}]
            """).ShouldBe([0, 9047, 9049, 9049], "по одному номеру на правило: у двух правил номер бывает один");

        var calls = new List<string>();
        var rules = new IpRules(arguments =>
        {
            calls.Add(string.Join(' ', arguments));
            return arguments switch
            {
                ["-j", "-4", ..] => """[{"priority":0},{"priority":9000},{"priority":9047},{"priority":9049},{"priority":9049},{"priority":9058},{"priority":32766}]""",
                ["-j", "-6", ..] => throw new IOException("ip: Address family not supported by protocol"),
                _ => string.Empty,
            };
        });

        rules.Remove(SingBoxConfigBuilder.RuleIndex, SingBoxConfigBuilder.RuleLast).ShouldBe(3);
        calls.ShouldBe(["-j -4 rule show", "-4 rule del priority 9047", "-4 rule del priority 9049", "-4 rule del priority 9049", "-j -6 rule show"],
            "9000 — у другой программы на sing-box, 9058 и дальше — не наши; IPv6 выключен в ядре — не ошибка");
    }

    [Fact]
    public void Rules_left_in_a_namespace_are_removed_and_others_stay()
    {
        NetworkNamespace.SkipUnlessAvailable("ip");
        using var netns = NetworkNamespace.Create();

        // Как после убитого sing-box: правила на свою таблицу остались, адаптера и маршрутов уже нет.
        netns.Run(null, "ip", "rule", "add", "priority", "9047", "to", "172.19.0.0/30", "lookup", "20551");
        netns.Run(null, "ip", "rule", "add", "priority", "9048", "lookup", "20551", "suppress_prefixlength", "0");
        netns.Run(null, "ip", "rule", "add", "priority", "9057", "nop");
        netns.Run(null, "ip", "rule", "add", "priority", "9000", "lookup", "2022");

        IpRules.ForThisMachine(netns.Path)!.Remove(SingBoxConfigBuilder.RuleIndex, SingBoxConfigBuilder.RuleLast).ShouldBe(3);

        var left = netns.Run(null, "ip", "rule", "show");
        left.ShouldNotContain("20551");
        left.ShouldNotContain("9057:");
        left.ShouldContain("9000:", Case.Sensitive, "правило другой программы на sing-box — не наше");
    }

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "Intel(R) Ethernet Connection I219-V", false)]
    [InlineData(NetworkInterfaceType.Wireless80211, "Intel(R) Wi-Fi 6 AX201 160MHz", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "Hyper-V Virtual Ethernet Adapter", false)]
    [InlineData(NetworkInterfaceType.Loopback, "Software Loopback Interface 1", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "Microsoft ISATAP Adapter", false)]
    [InlineData((NetworkInterfaceType)53, "WireGuard Tunnel", true)]
    [InlineData((NetworkInterfaceType)53, "Wintun Userspace Tunnel", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "TAP-Windows Adapter V9", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter for Windows x64", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "Fortinet SSL VPN Virtual Ethernet Adapter", true)]
    [InlineData(NetworkInterfaceType.Ppp, "WAN Miniport (L2TP)", true)]
    [InlineData(NetworkInterfaceType.Tunnel, "Some Tunnel Adapter", true)]
    public void Windows_adapter_is_a_tunnel_by_type_or_driver(NetworkInterfaceType type, string description, bool tunnel) =>
        WindowsRouteTable.IsTunnel(type, description).ShouldBe(tunnel);
}
