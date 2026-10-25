using System.Text.Json;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Маршруты Linux через <c>ip -j</c>: все таблицы, IPv4 и IPv6 — wg-quick, sing-box и другие VPN держат
/// маршрут ко всему интернету в своей таблице по правилу, а не в основной. Туннель — по виду адаптера:
/// TUN и TAP, WireGuard, PPP и туннели ядра (ipip, gre). Выключенные адаптеры не считаются.
/// </summary>
/// <param name="ip">Вызов ip: аргументы → вывод; не вышло — <see cref="IOException"/> с текстом ip.</param>
public sealed class IpRouteTable(Func<IReadOnlyList<string>, string> ip) : IRouteTable
{
    // Половины интернета: VPN ставит две и перебивает основной маршрут, не удаляя его.
    private static readonly string[] Halves = ["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"];

    // sit — туннель IPv6 через IPv4 (брокеры IPv6), не VPN; ipip и gre с маршрутом ко всему интернету — VPN своими руками.
    private static readonly string[] TunnelLinks = ["none", "ppp", "ipip", "tunnel6", "gre", "gre6"];
    private static readonly string[] TunnelKinds = ["tun", "wireguard", "ppp"];

    /// <summary>ip этой машины (<see cref="IpCommand"/>); нет ip — null.</summary>
    public static IpRouteTable? ForThisMachine() => IpCommand.ForThisMachine() is { } ip ? new IpRouteTable(ip) : null;

    public IReadOnlyList<DefaultRoute> DefaultRoutes()
    {
        try
        {
            var links = Links(ip(["-j", "-d", "link", "show"]));
            var routes = Devices(ip(["-j", "-4", "route", "show", "table", "all"])).ToList();
            try
            {
                routes.AddRange(Devices(ip(["-j", "-6", "route", "show", "table", "all"])));
            }
            catch (IOException)
            {
                // IPv6 в ядре выключен — маршрутов IPv6 нет.
            }

            return [.. routes.Where(links.ContainsKey).Select(device => new DefaultRoute(device, links[device]))];
        }
        catch (JsonException e)
        {
            throw new IOException("ip: непонятный вывод — " + e.Message, e);
        }
    }

    /// <summary>Включенные адаптеры из <c>ip -j -d link show</c>: имя → туннель ли.</summary>
    public static Dictionary<string, bool> Links(string json)
    {
        var links = new Dictionary<string, bool>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        foreach (var link in document.RootElement.EnumerateArray())
        {
            if (Text(link, "ifname") is not { } name
                || !link.TryGetProperty("flags", out var flags) || !flags.EnumerateArray().Any(f => f.GetString() == "UP"))
            {
                continue;
            }

            var kind = link.TryGetProperty("linkinfo", out var info) ? Text(info, "info_kind") : null;
            links[name] = TunnelLinks.Contains(Text(link, "link_type")) || TunnelKinds.Contains(kind);
        }

        return links;
    }

    /// <summary>Адаптеры маршрутов ко всему интернету из <c>ip -j route show table all</c>; блокирующие маршруты без адаптера — мимо.</summary>
    public static IEnumerable<string> Devices(string json)
    {
        using var document = JsonDocument.Parse(json);
        var devices = new List<string>();
        foreach (var route in document.RootElement.EnumerateArray())
        {
            if (Text(route, "type") is { } type && type != "unicast"
                || Text(route, "dst") is not { } destination || (destination != "default" && !Halves.Contains(destination)))
            {
                continue;
            }

            if (Text(route, "dev") is { } device)
            {
                devices.Add(device);
            }

            // Маршрут через несколько шлюзов — адаптер у каждого.
            if (route.TryGetProperty("nexthops", out var hops))
            {
                devices.AddRange(hops.EnumerateArray().Select(hop => Text(hop, "dev")).OfType<string>());
            }
        }

        return devices;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
