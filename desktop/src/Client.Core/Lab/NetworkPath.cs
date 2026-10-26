using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Core.Lab;

/// <summary>Звено пути трафика от машины до сайта.</summary>
public enum PathNodeKind
{
    /// <summary>Этот компьютер.</summary>
    Device,

    /// <summary>Сетевой адаптер, через который трафик уходит из машины (Wi-Fi, кабель, сотовая связь).</summary>
    Adapter,

    /// <summary>Роутер — шлюз локальной сети.</summary>
    Router,

    /// <summary>Провайдер интернета — владелец реального адреса.</summary>
    Provider,

    /// <summary>VPN-сервер — выход защиты в интернет.</summary>
    Vpn,

    /// <summary>Сайт, до которого строится путь.</summary>
    Site,
}

/// <summary>Откуда известно звено или участок.</summary>
public enum Evidence
{
    /// <summary>Измерено на этой машине: сведения ОС, замер службы, ответ узла.</summary>
    Measured,

    /// <summary>Условно: так устроено почти всегда, но этот путь не измерен — модель, а не замер.</summary>
    Conditional,
}

/// <summary>Звено пути.</summary>
/// <param name="Kind">Что за звено.</param>
/// <param name="Name">Имя звена: адаптер, провайдер, сайт; неизвестно — null (тогда звено названо по виду).</param>
/// <param name="Address">Адрес звена (шлюз, реальный IP, адрес выхода, адрес сайта); неизвестен — null.</param>
/// <param name="Detail">Подробность: вид адаптера, AS провайдера, город сервера, итог неудачной проверки сайта; нет — null.</param>
/// <param name="Evidence">Измерено или условно.</param>
public sealed record PathNode(PathNodeKind Kind, string? Name, IPAddress? Address, string? Detail, Evidence Evidence);

/// <summary>Участок пути между соседними звеньями.</summary>
/// <param name="From">Откуда.</param>
/// <param name="To">Куда.</param>
/// <param name="Evidence">Измерен (ответ пришел через этот участок) или условно.</param>
/// <param name="Tunnel">Участок идет внутри туннеля защиты — зашифрован и не виден роутеру и провайдеру.</param>
/// <param name="Time">Время ответа по участку, если его измерили; нет — null.</param>
/// <param name="Hops">Сколько промежуточных узлов на участке ответили при трассировке; не трассировали — null.</param>
public sealed record PathLink(PathNodeKind From, PathNodeKind To, Evidence Evidence, bool Tunnel, TimeSpan? Time = null, int? Hops = null);

/// <summary>Путь трафика от машины до сайта: звенья по порядку и участки между ними.</summary>
public sealed record NetworkPath(IReadOnlyList<PathNode> Nodes, IReadOnlyList<PathLink> Links)
{
    /// <summary>Есть ли на пути защита (VPN-сервер).</summary>
    public bool Protected => Nodes.Any(n => n.Kind == PathNodeKind.Vpn);

    /// <summary>Идет ли трафик машины в туннель (режим VPN): тогда программа отдает его TUN-адаптеру, а ядро шифрует.</summary>
    public bool Tunneled => Links.Any(l => l.Tunnel);
}

/// <summary>Что известно для построения пути.</summary>
/// <param name="Snapshot">Сеть машины от службы; нет — null.</param>
/// <param name="Status">Состояние защиты.</param>
/// <param name="Provider">Сведения о реальном адресе (RIPEstat); не спрашивали — null.</param>
/// <param name="Exit">Сведения об адресе выхода; не спрашивали — null.</param>
/// <param name="Site">Сайт: имя или адрес; не задан — null.</param>
/// <param name="SiteCheck">Замер сайта (TCP или TLS); не мерили — null.</param>
/// <param name="Trace">Трассировка до сайта; не мерили — null.</param>
public sealed record PathFacts(
    NetworkSnapshot? Snapshot,
    ConnectionStatus Status,
    AddressInfo? Provider = null,
    AddressInfo? Exit = null,
    string? Site = null,
    ProbeStep? SiteCheck = null,
    IReadOnlyList<ProbeStep>? Trace = null);

/// <summary>
/// Путь трафика из фактов (Network Lab, этап 8): звено или участок «измерено» только тогда, когда за ним стоит сведение ОС
/// или замер, иначе — «условно»: модель не выдается за измерение. Замер засчитывается только тому участку, по которому
/// шел: трассировка в туннеле не меряет роутер, проверка сайта в режиме прокси (напрямую) не меряет участок от сервера.
/// </summary>
public static class PathBuilder
{
    public static NetworkPath Build(PathFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var status = facts.Status;
        var protectedNow = status is { State: ConnectionState.Connected, IsPaused: false };

        // Туннель — от адаптера машины до VPN-сервера, когда защита в режиме VPN; в режиме прокси через сервер идут только программы
        // с прокси, а замеры службы — напрямую.
        var tunnel = protectedNow && status.Mode == ConnectionMode.Vpn;

        // Трассировка в туннеле уходит к серверу защиты, а не по проводу (ICMP протоколы прокси не переносят): роутер и
        // провайдер по ней не измерены.
        var trace = tunnel ? null : facts.Trace;

        // Адаптер выхода — карта с маршрутом по умолчанию, не туннель: туннель — в звене VPN.
        var card = Physical(facts.Snapshot);
        var router = card?.Gateways.FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork)
            ?? (card is { Gateways.Count: > 0 } ? card.Gateways[0] : null);
        var real = IPAddress.TryParse(status.Exit.DirectIp, out var direct) ? direct : null;
        var exit = protectedNow && IPAddress.TryParse(status.Exit.Ip, out var seen) ? seen : null;

        var nodes = new List<PathNode>
        {
            new(PathNodeKind.Device, null, null, null, Evidence.Measured),
            new(PathNodeKind.Adapter, card?.Name, card?.Addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address,
                card?.Kind.ToString(), card is null ? Evidence.Conditional : Evidence.Measured),
            new(PathNodeKind.Router, null, router, null, router is null ? Evidence.Conditional : Evidence.Measured),
            new(PathNodeKind.Provider, facts.Provider?.Holder, real, Asn(facts.Provider),
                real is not null || facts.Provider is { Failure: null } ? Evidence.Measured : Evidence.Conditional),
        };
        if (protectedNow)
        {
            nodes.Add(new PathNode(PathNodeKind.Vpn, facts.Exit?.Holder, exit, status.Exit.City ?? facts.Exit?.City,
                exit is null ? Evidence.Conditional : Evidence.Measured));
        }

        // Сайт ответил — звено измерено. Адрес FakeDNS (198.18.0.0/15) — подставной адрес ядра, не адрес сайта: его не показываем.
        var siteOk = facts.SiteCheck is { Outcome: ProbeOutcome.Ok };
        var siteAddress = facts.SiteCheck?.Address is { } address && !IsFake(address) ? address : null;
        var siteFailure = facts.SiteCheck is { Outcome: not ProbeOutcome.Ok } failed ? failed.Outcome.ToString() : null;
        nodes.Add(new PathNode(PathNodeKind.Site, facts.Site, siteAddress, siteFailure, siteOk ? Evidence.Measured : Evidence.Conditional));

        var hops = trace?.Count(s => s.Outcome is ProbeOutcome.Hop or ProbeOutcome.Ok);
        var siteTime = siteOk ? facts.SiteCheck!.Time : null;
        var links = new List<PathLink>
        {
            // Внутри компьютера трафик еще не зашифрован: туннель начинается на выходе из ядра.
            new(PathNodeKind.Device, PathNodeKind.Adapter, Evidence.Measured, false),
            new(PathNodeKind.Adapter, PathNodeKind.Router, router is null ? Evidence.Conditional : Evidence.Measured, tunnel,
                trace?.FirstOrDefault(s => s.Index == 1 && s.Outcome is ProbeOutcome.Hop or ProbeOutcome.Ok)?.Time),
            new(PathNodeKind.Router, PathNodeKind.Provider, hops is > 1 ? Evidence.Measured : Evidence.Conditional, tunnel, null, hops),
        };
        if (protectedNow)
        {
            // Проверка сайта в режиме прокси идет напрямую, мимо сервера: участок «сервер → сайт» ею не измерен.
            links.Add(new PathLink(PathNodeKind.Provider, PathNodeKind.Vpn, exit is null ? Evidence.Conditional : Evidence.Measured, tunnel));
            links.Add(new PathLink(PathNodeKind.Vpn, PathNodeKind.Site, siteOk && tunnel ? Evidence.Measured : Evidence.Conditional, false,
                tunnel ? siteTime : null));
        }
        else
        {
            links.Add(new PathLink(PathNodeKind.Provider, PathNodeKind.Site, siteOk ? Evidence.Measured : Evidence.Conditional, false, siteTime));
        }

        return new NetworkPath(nodes, links);
    }

    /// <summary>
    /// Адаптер, через который трафик уходит из машины: включенный, не туннель и не петля, с маршрутом по умолчанию; маршрутов
    /// нет — первый включенный с шлюзом.
    /// </summary>
    public static NetworkCard? Physical(NetworkSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        var candidates = snapshot.Cards.Where(c => c.IsUp && c.Kind is not (AdapterKind.Tunnel or AdapterKind.Loopback)).ToList();
        var defaults = snapshot.Routes.Where(r => r.Destination is "default" or "0.0.0.0/0" or "::/0").Select(r => r.Interface).ToHashSet(StringComparer.Ordinal);
        return candidates.FirstOrDefault(c => defaults.Contains(c.Name)) ?? candidates.FirstOrDefault(c => c.Gateways.Count > 0);
    }

    // Подставные адреса FakeDNS ядра — из служебного диапазона 198.18.0.0/15.
    private static bool IsFake(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [198, 18 or 19, _, _];

    private static string? Asn(AddressInfo? info) => info is { Asn: { } asn } ? "AS" + asn.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
}
