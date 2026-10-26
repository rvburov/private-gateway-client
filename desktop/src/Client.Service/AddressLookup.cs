using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Что известно об адресе в интернете (сетевой инспектор, этап 7): автономная система, ее владелец и объявленная сеть —
/// RIPEstat «prefix-overview», примерное место — RIPEstat «maxmind-geo-lite». Спрашивает только по просьбе пользователя и
/// только о публичном адресе: частный, служебный или адрес документации — ответ сразу, без запроса. В запросе — только
/// сам адрес, уже разобранный как IP. Ответ помнится час: провайдер у адреса меняется редко, а повторное нажатие не должно
/// снова ходить в интернет. Источник не ответил — не запоминается: можно спросить еще раз.
/// </summary>
/// <param name="fetch">Запрос через вход ядра или напрямую (порт 0): тело ответа, нет ответа — null.</param>
/// <param name="time">Часы памяти ответов.</param>
/// <param name="source">Адрес RIPEstat с «/data/» на конце; null — <see cref="DefaultSource"/>.</param>
public sealed class AddressLookup(Func<ServerProbe, CancellationToken, Task<string?>> fetch, TimeProvider time, Uri? source = null)
{
    /// <summary>Открытый API RIPE NCC: без ключа, без учетной записи.</summary>
    public static readonly Uri DefaultSource = new("https://stat.ripe.net/data/");

    /// <summary>Сколько помнить ответ.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    /// <summary>Сколько ждать каждый из двух запросов.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // Не публичные IPv4: «этот» адрес, частные, CGNAT, петля, link-local, служебные IETF, документация, стенды, групповые и
    // резерв (RFC 6890).
    private static readonly IPNetwork[] Special4 =
    [
        .. new[]
        {
            "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24",
            "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3",
        }.Select(n => IPNetwork.Parse(n)),
    ];

    // IPv6 публичен только в глобальном одноадресном 2000::/3 — кроме служебных IETF (и Teredo) и документации.
    private static readonly IPNetwork Global6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] Special6 = [IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("3fff::/20")];

    private readonly ConcurrentDictionary<IPAddress, (AddressInfo Info, DateTimeOffset At)> _known = new();
    private readonly Uri _source = source ?? DefaultSource;

    /// <summary>
    /// Сведения об <paramref name="address"/>; запросы — через вход ядра <paramref name="port"/> с
    /// <paramref name="credentials"/>, порт 0 — обычным путем службы.
    /// </summary>
    public async Task<AddressInfo> DescribeAsync(IPAddress address, int port, NetworkCredential? credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!IsPublic(address))
        {
            return AddressInfo.Failed(address, AddressFailure.Private);
        }

        var now = time.GetUtcNow();
        if (_known.TryGetValue(address, out var known) && now - known.At < Keep)
        {
            return known.Info;
        }

        // В адресе запроса — только IP в каноническом виде, ничего от пользователя как текста.
        var resource = Uri.EscapeDataString(address.ToString());
        var overview = fetch(new ServerProbe(port, credentials, new Uri(_source, $"prefix-overview/data.json?resource={resource}&sourceapp=private-gateway"), Timeout), cancellationToken);
        var geo = fetch(new ServerProbe(port, credentials, new Uri(_source, $"maxmind-geo-lite/data.json?resource={resource}&sourceapp=private-gateway"), Timeout), cancellationToken);
        var info = Parse(address, await overview.ConfigureAwait(false), await geo.ConfigureAwait(false));
        if (info.Failure != AddressFailure.Unreachable)
        {
            _known[address] = (info, now);
        }

        return info;
    }

    /// <summary>Публичный ли адрес: о частном, служебном и адресе документации в интернете ничего нет — и спрашивать незачем.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !Special4.Any(n => n.Contains(address)),
            AddressFamily.InterNetworkV6 => Global6.Contains(address) && !Special6.Any(n => n.Contains(address)),
            _ => false,
        };
    }

    /// <summary>
    /// Сведения из ответов RIPEstat. Объявленной сети нет — ни ASN, ни владельца; страна «??» или пустая — неизвестна. Нет ни
    /// одного ответа — <see cref="AddressFailure.Unreachable"/>; ответы есть, а сведений нет — <see cref="AddressFailure.NotFound"/>.
    /// </summary>
    public static AddressInfo Parse(IPAddress address, string? overview, string? geo)
    {
        ArgumentNullException.ThrowIfNull(address);
        var (asn, holder, prefix) = Overview(overview);
        var (country, city) = Location(geo);
        return overview is null && geo is null ? AddressInfo.Failed(address, AddressFailure.Unreachable)
            : asn is null && country is null ? AddressInfo.Failed(address, AddressFailure.NotFound)
            : new AddressInfo(address, asn, holder, prefix, country, city, null);
    }

    /// <summary>Через вход ядра (порт не 0) или напрямую: тело ответа 2xx; нет ответа или ответ не 2xx — null.</summary>
    public static async Task<string?> FetchAsync(ServerProbe probe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        using var handler = new SocketsHttpHandler
        {
            Proxy = probe.Port == 0 ? null : new LatencyProbe.FixedProxy(new Uri($"socks5://127.0.0.1:{probe.Port}")) { Credentials = probe.Credentials },
            UseProxy = probe.Port != 0,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var http = new HttpClient(handler) { Timeout = probe.Timeout };
        try
        {
            using var response = await http.GetAsync(probe.Target, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static (long? Asn, string? Holder, string? Prefix) Overview(string? json)
    {
        if (Data(json) is not { } data)
        {
            return (null, null, null);
        }

        long? asn = null;
        string? holder = null;
        if (data.TryGetProperty("asns", out var asns) && asns.ValueKind == JsonValueKind.Array && asns.GetArrayLength() > 0
            && asns[0].TryGetProperty("asn", out var number) && number.TryGetInt64(out var value) && value > 0)
        {
            asn = value;
            holder = Text(asns[0], "holder");
        }

        // Сеть — только объявленная: без объявления RIPEstat возвращает сам адрес.
        var announced = data.TryGetProperty("announced", out var flag) && flag.ValueKind == JsonValueKind.True;
        var prefix = announced && Text(data, "resource") is { } resource && resource.Contains('/', StringComparison.Ordinal) ? resource : null;
        return (asn, holder, prefix);
    }

    private static (string? Country, string? City) Location(string? json)
    {
        if (Data(json) is not { } data || !data.TryGetProperty("located_resources", out var located) || located.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var resource in located.EnumerateArray())
        {
            if (!resource.TryGetProperty("locations", out var locations) || locations.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var location in locations.EnumerateArray())
            {
                if (Text(location, "country") is { Length: 2 } country && country.All(char.IsAsciiLetter))
                {
                    return (country.ToUpperInvariant(), Text(location, "city"));
                }
            }
        }

        return (null, null);
    }

    // «data» ответа со статусом «ok»; другое или не JSON — null.
    private static JsonElement? Data(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "status") == "ok"
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text
            ? text
            : null;
}
