using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Сетевой инспектор: снимок сети машины — адаптеры (.NET: адреса, шлюзы, DNS, MAC, MTU, скорость), аренда DHCP и маршруты
/// (подробности ОС, <see cref="INetworkDetails"/>). Только читает: ни адаптеры, ни маршруты не трогает. Сведения, которые
/// ОС не отдает (свойство не поддерживается, адаптер пропал во время чтения), просто пусты.
/// </summary>
/// <param name="details">Маршруты и DHCP этой ОС; null — без них.</param>
/// <param name="time">Часы для времени снимка.</param>
/// <param name="journal">Журнал службы — для отказа чтения маршрутов.</param>
/// <param name="adapters">Адаптеры машины (тесты дают свои); null — <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.</param>
public sealed class NetworkInspector(INetworkDetails? details, TimeProvider time, ServiceJournal journal, Func<IReadOnlyList<NetworkInterface>>? adapters = null)
{
    // Туннели по имени: на Linux и macOS .NET называет их Unknown или Ethernet.
    private static readonly string[] TunnelNames = ["tun", "tap", "wg", "utun", "ppp", "ipsec", "gif", "zt", "tailscale", "pgtun"];
    private static readonly string[] CellularNames = ["wwan", "rmnet", "pdp_ip", "ccmni"];
    private static readonly string[] WifiNames = ["wl", "wlan", "wifi"];

    public NetworkSnapshot Take()
    {
        var cards = (adapters ?? NetworkInterface.GetAllNetworkInterfaces)().Select(Card).ToList();
        IReadOnlyList<NetworkRoute> routes = [];
        try
        {
            routes = details?.Routes() ?? [];
        }
        catch (IOException e)
        {
            journal.Add(JournalLevel.Warning, "service", "Инспектор: маршруты не прочитаны — " + e.Message);
        }

        return new NetworkSnapshot(time.GetUtcNow(), cards, routes);
    }

    /// <summary>Вид адаптера: по типу от ОС, а где тип ничего не говорит (Linux, macOS) — по имени и описанию.</summary>
    public static AdapterKind Kind(NetworkInterfaceType type, string name, string description)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(description);
        bool Named(string[] prefixes) => prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return type switch
        {
            NetworkInterfaceType.Loopback => AdapterKind.Loopback,
            NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp => AdapterKind.Tunnel,
            _ when Named(TunnelNames) || description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) || description.Contains("TAP-", StringComparison.OrdinalIgnoreCase) => AdapterKind.Tunnel,
            NetworkInterfaceType.Wireless80211 => AdapterKind.Wifi,
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => AdapterKind.Cellular,
            _ when Named(CellularNames) => AdapterKind.Cellular,
            _ when Named(WifiNames) => AdapterKind.Wifi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.GigabitEthernet => AdapterKind.Ethernet,
            _ when name is "lo" or "lo0" => AdapterKind.Loopback,
            _ => AdapterKind.Other,
        };
    }

    /// <summary>Шлюзы адаптера: «0.0.0.0» и «::» — это «шлюза нет» (так ОС пишет сеть без шлюза), их нет и в списке.</summary>
    public static List<IPAddress> Gateways(IEnumerable<IPAddress> gateways) =>
        [.. gateways.Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))];

    /// <summary>MAC вида aa:bb:cc:dd:ee:ff; у туннелей и петли (нет байтов или все нули) — null.</summary>
    public static string? Mac(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return bytes.Length == 0 || bytes.All(b => b == 0) ? null : string.Join(':', bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    private NetworkCard Card(NetworkInterface adapter)
    {
        var properties = Try(adapter.GetIPProperties);
        var addresses = Try(() => properties?.UnicastAddresses.Select(a => new NetworkAddress(a.Address, a.PrefixLength)).ToList()) ?? [];
        var gateways = Try(() => Gateways(properties?.GatewayAddresses.Select(g => g.Address) ?? [])) ?? [];
        var dns = Try(() => properties?.DnsAddresses.ToList()) ?? [];
        var mtu = Try(() => (int?)properties?.GetIPv4Properties()?.Mtu) ?? Try(() => (int?)properties?.GetIPv6Properties()?.Mtu);
        var speed = Try(() => (long?)adapter.Speed) is > 0 and var known ? known : (long?)null;
        var kind = Kind(adapter.NetworkInterfaceType, adapter.Name, adapter.Description);
        var mac = kind is AdapterKind.Tunnel or AdapterKind.Loopback ? null : Try(() => Mac(adapter.GetPhysicalAddress().GetAddressBytes()));
        var dhcp = kind == AdapterKind.Loopback || addresses.All(a => a.Address.AddressFamily != AddressFamily.InterNetwork) ? null : Try(() => details?.Dhcp(adapter.Name));
        return new NetworkCard(adapter.Name, adapter.Description == adapter.Name ? string.Empty : adapter.Description, kind,
            Try(() => adapter.OperationalStatus) == OperationalStatus.Up, mac, mtu is > 0 ? mtu : null, speed, addresses, gateways, dns, dhcp);
    }

    // Свойство, которого эта ОС не дает, или адаптер, пропавший во время чтения, — пустое значение, а не отказ всего снимка.
    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or IOException or InvalidOperationException)
        {
            return default;
        }
    }
}
