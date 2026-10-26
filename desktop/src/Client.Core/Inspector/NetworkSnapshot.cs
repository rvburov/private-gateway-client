using System.Net;

namespace PrivateGateway.Client.Core.Inspector;

/// <summary>Вид адаптера для сетевого инспектора.</summary>
public enum AdapterKind
{
    Ethernet,
    Wifi,
    Cellular,

    /// <summary>Туннель: TUN, WireGuard, PPP, TAP — свой или чужой VPN.</summary>
    Tunnel,

    Loopback,
    Other,
}

/// <summary>Адрес адаптера с длиной префикса сети.</summary>
public sealed record NetworkAddress(IPAddress Address, int PrefixLength)
{
    public override string ToString() => $"{Address}/{PrefixLength}";
}

/// <summary>
/// Адрес получен автоматически (DHCP, у IPv6 — и объявление роутера): сервер — если ОС его называет, остаток аренды — если
/// ОС его знает.
/// </summary>
public sealed record DhcpLease(IPAddress? Server, TimeSpan? Remaining);

/// <summary>Адаптер машины: как его видит ОС, без изменений.</summary>
/// <param name="Name">Имя адаптера в ОС (eth0, en0, «Ethernet»).</param>
/// <param name="Description">Описание драйвера или модели; нет — пусто.</param>
/// <param name="Kind">Вид адаптера.</param>
/// <param name="IsUp">Адаптер включен и на связи.</param>
/// <param name="Mac">MAC-адрес вида 00:11:22:33:44:55; у туннелей и петли — null.</param>
/// <param name="Mtu">Наибольший размер пакета; неизвестен — null.</param>
/// <param name="SpeedBitsPerSecond">Скорость канала по словам драйвера; неизвестна — null.</param>
/// <param name="Addresses">Адреса IPv4 и IPv6 с префиксами.</param>
/// <param name="Gateways">Шлюзы адаптера.</param>
/// <param name="DnsServers">Серверы DNS, которые ОС назначила адаптеру.</param>
/// <param name="Dhcp">Адрес получен автоматически — что известно об аренде; адрес задан вручную или неизвестно — null.</param>
public sealed record NetworkCard(
    string Name,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    string? Mac,
    int? Mtu,
    long? SpeedBitsPerSecond,
    IReadOnlyList<NetworkAddress> Addresses,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    DhcpLease? Dhcp);

/// <summary>Маршрут: куда (сеть с префиксом или default), через какой шлюз (нет — сеть рядом), какой адаптер, метрика.</summary>
public sealed record NetworkRoute(string Destination, IPAddress? Gateway, string Interface, int? Metric);

/// <summary>Сеть машины на момент <paramref name="TakenAt"/>: адаптеры и маршруты (IPv4 и IPv6).</summary>
public sealed record NetworkSnapshot(DateTimeOffset TakenAt, IReadOnlyList<NetworkCard> Cards, IReadOnlyList<NetworkRoute> Routes);
