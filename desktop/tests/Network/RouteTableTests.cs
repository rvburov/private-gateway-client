using System.Net.NetworkInformation;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Маршруты машины для поиска чужого VPN: разбор вывода <c>ip -j</c> (Linux), настоящий туннель в своем
/// сетевом пространстве Linux, таблица маршрутов Windows (CI) и признаки туннеля у адаптеров Windows.
/// </summary>
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
