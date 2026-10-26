using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Core.Logging;

/// <summary>
/// Обезличивание отчета: публичные адреса IPv4 и все адреса IPv6 (в них бывает MAC или постоянный номер машины) — метками
/// «[адрес-1]», «[адрес-2]»… — один и тот же адрес везде одной меткой, чтобы по отчету было видно «сменился ли адрес», но не
/// какой он; MAC — «[MAC]». Частные и служебные IPv4 (локальная сеть, туннель, петля), известные открытые резолверы и
/// широкие сети маршрутов (/8 и шире: 0.0.0.0/1 и 128.0.0.0/1 у туннеля) остаются: без них отчет не объяснит маршрут.
/// Секреты и ссылки потом скрывает <see cref="SecretMasker"/>.
/// </summary>
public sealed partial class ReportAnonymizer
{
    // Открытые резолверы и проверочные адреса: известны всем и ничего не говорят о пользователе.
    private static readonly HashSet<IPAddress> WellKnown =
    [
        .. new[]
        {
            "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "149.112.112.112", "2606:4700:4700::1111", "2606:4700:4700::1001",
            "2001:4860:4860::8888", "2001:4860:4860::8844",
        }.Select(IPAddress.Parse),
    ];

    private readonly Dictionary<IPAddress, string> _tokens = [];
    private readonly string _label;

    /// <param name="label">Слово метки: «адрес» или «address».</param>
    public ReportAnonymizer(string label = "адрес")
    {
        ArgumentException.ThrowIfNullOrEmpty(label);
        _label = label;
    }

    /// <summary>Строка с метками вместо личных адресов и MAC и без секретов.</summary>
    public string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = Mac().Replace(text, "[MAC]");
        result = Ipv6().Replace(result, m => IPAddress.TryParse(m.Groups["address"].Value.Split('%')[0], out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
            ? Keep(address) || Wide(m) ? m.Value : Token(address) + m.Groups["prefix"].Value
            : m.Value);
        result = Ipv4().Replace(result, m =>
        {
            var address = IPAddress.Parse(m.Groups["address"].Value);
            return Keep(address) || Wide(m) ? m.Value : Token(address) + m.Groups["prefix"].Value;
        });
        return SecretMasker.Mask(result);
    }

    /// <summary>Адрес остается в отчете: частный или служебный IPv4, петля и пустой IPv6, известный открытый резолвер.</summary>
    public static bool Keep(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (WellKnown.Contains(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any);
        }

        var b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] >= 224
            || b[0] == 100 && b[1] >= 64 && b[1] < 128
            || b[0] == 169 && b[1] == 254
            || b[0] == 172 && b[1] >= 16 && b[1] < 32
            || b[0] == 192 && b[1] == 168
            || b[0] == 198 && b[1] is 18 or 19;
    }

    // Сеть маршрута шириной /8 и больше — общая для всех, а не адрес пользователя.
    private static bool Wide(Match match) =>
        match.Groups["prefix"].Success && int.TryParse(match.Groups["prefix"].ValueSpan[1..], out var length) && length <= 8;

    private string Token(IPAddress address)
    {
        if (!_tokens.TryGetValue(address, out var token))
        {
            token = $"[{_label}-{_tokens.Count + 1}]";
            _tokens[address] = token;
        }

        return token;
    }

    // MAC: шесть пар через двоеточие или дефис.
    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b")]
    private static partial Regex Mac();

    // IPv4 — четыре числа до 255, и длина префикса сети, если есть.
    [GeneratedRegex(@"(?<![\d.])(?<address>(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3})(?<prefix>/\d{1,2}(?!\d))?(?![\d.])")]
    private static partial Regex Ipv4();

    // IPv6 — группы через двоеточие, с «::», зоной и длиной префикса; разбор решает, адрес ли это.
    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?<address>(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%[0-9A-Za-z]+)?)(?<prefix>/\d{1,3}(?!\d))?(?![0-9A-Fa-f:])")]
    private static partial Regex Ipv6();
}
