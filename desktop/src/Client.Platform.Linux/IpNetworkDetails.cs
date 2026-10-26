using System.Net;
using System.Text.Json;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Подробности сети Linux для инспектора через <c>ip -j</c>: маршруты основной таблицы (IPv4 и IPv6) и аренда адреса —
/// адрес с флагом dynamic получен автоматически (DHCP, у IPv6 — и объявление роутера), остаток — его срок жизни. Сервер
/// DHCP ip не называет.
/// </summary>
/// <param name="ip">Вызов ip: аргументы → вывод; не вышло — <see cref="IOException"/>.</param>
public sealed class IpNetworkDetails(Func<IReadOnlyList<string>, string> ip) : INetworkDetails
{
    // Срок «навсегда» у адресов, заданных вручную.
    private const long Forever = uint.MaxValue;

    /// <summary>ip этой машины (<see cref="IpCommand"/>); нет ip — null.</summary>
    public static IpNetworkDetails? ForThisMachine() => IpCommand.ForThisMachine() is { } ip ? new IpNetworkDetails(ip) : null;

    public IReadOnlyList<NetworkRoute> Routes()
    {
        try
        {
            var routes = ParseRoutes(ip(["-j", "-4", "route", "show", "table", "main"])).ToList();
            try
            {
                routes.AddRange(ParseRoutes(ip(["-j", "-6", "route", "show", "table", "main"])));
            }
            catch (IOException)
            {
                // IPv6 в ядре выключен — маршрутов IPv6 нет.
            }

            return routes;
        }
        catch (JsonException e)
        {
            throw new IOException("ip: непонятный вывод — " + e.Message, e);
        }
    }

    public DhcpLease? Dhcp(string adapter)
    {
        ArgumentException.ThrowIfNullOrEmpty(adapter);
        try
        {
            return ParseLease(ip(["-j", "-4", "addr", "show", "dev", adapter]));
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Маршруты из <c>ip -j route show</c>: у маршрута через несколько шлюзов — по строке на шлюз; блокирующие — мимо.</summary>
    public static IEnumerable<NetworkRoute> ParseRoutes(string json)
    {
        using var document = JsonDocument.Parse(json);
        var routes = new List<NetworkRoute>();
        foreach (var route in document.RootElement.EnumerateArray())
        {
            if (Text(route, "type") is { } type && type != "unicast" || Text(route, "dst") is not { } destination)
            {
                continue;
            }

            int? metric = route.TryGetProperty("metric", out var value) && value.TryGetInt32(out var number) ? number : null;
            if (route.TryGetProperty("nexthops", out var hops))
            {
                routes.AddRange(hops.EnumerateArray().Where(h => Text(h, "dev") is not null)
                    .Select(h => new NetworkRoute(destination, Address(Text(h, "gateway")), Text(h, "dev")!, metric)));
            }
            else if (Text(route, "dev") is { } device)
            {
                routes.Add(new NetworkRoute(destination, Address(Text(route, "gateway")), device, metric));
            }
        }

        return routes;
    }

    /// <summary>
    /// Аренда из <c>ip -j addr show dev …</c>: первый адрес с флагом dynamic; срок «навсегда» — остаток неизвестен. Адреса без
    /// флага заданы вручную — null.
    /// </summary>
    public static DhcpLease? ParseLease(string json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var link in document.RootElement.EnumerateArray())
        {
            if (!link.TryGetProperty("addr_info", out var addresses))
            {
                continue;
            }

            foreach (var address in addresses.EnumerateArray())
            {
                if (address.TryGetProperty("dynamic", out var dynamic) && dynamic.ValueKind == JsonValueKind.True)
                {
                    return new DhcpLease(null, address.TryGetProperty("valid_life_time", out var valid) && valid.TryGetInt64(out var seconds)
                        && seconds < Forever ? TimeSpan.FromSeconds(seconds) : null);
                }
            }
        }

        return null;
    }

    private static IPAddress? Address(string? text) => IPAddress.TryParse(text, out var address) ? address : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;
}
