using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Core.Logging;

/// <summary>
/// Скрывает адреса в выводе ядер: на уровнях «Информация» и «Отладка» Xray и sing-box пишут, к каким сайтам и
/// адресам идут соединения («tunneling request to tcp:example.com:443», «inbound connection to …»), а журнал
/// пользователь может отправить в отчете. IP (v4 и v6) и имена с доменом верхнего уровня из букв заменяются;
/// имена файлов (geoip.dat, default.go) и номера версий остаются.
/// </summary>
public static partial class AddressMasker
{
    /// <summary>Чем заменяется адрес.</summary>
    public const string Hidden = "•••";

    // Расширения файлов, похожие на домены верхнего уровня: geoip.dat, default.go, config.json.
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "go", "dat", "json", "txt", "log", "so", "dll", "exe", "yaml", "yml", "toml", "sock", "db",
    };

    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = Ipv6().Replace(text, m => IPAddress.TryParse(m.Groups["address"].Value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? m.Value.Replace(m.Groups["address"].Value, Hidden, StringComparison.Ordinal)
            : m.Value);
        result = Ipv4().Replace(result, Hidden);
        return Name().Replace(result, m => FileExtensions.Contains(m.Groups["top"].Value) ? m.Value : Hidden);
    }

    // Кандидаты в IPv6 — в скобках или без, с зоной; решает разбор адреса (время «14:40:32» им не является).
    [GeneratedRegex(@"(?<![\w:.])\[?(?<address>(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?:\.\d{1,3}){0,3}(?:%\w+)?)\]?(?![\w.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![\w.-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?<top>[a-z]{2,63})(?![\w-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Name();
}
