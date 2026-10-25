using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Core.Settings;

/// <summary>Куда уходят DNS-запросы в туннеле (доска 5).</summary>
public enum DnsChoice
{
    /// <summary>DNS из профиля сервера или подписки; нет в профиле — выбор службы.</summary>
    Profile,
    CloudflareDoh,
    Quad9Doh,

    /// <summary>Свой адрес — <see cref="ProtectionSettings.CustomDns"/>.</summary>
    Custom,
}

/// <summary>Приоритет адресов для сайтов с IPv4 и IPv6.</summary>
public enum AddressPreference
{
    PreferIpv4,
    PreferIpv6,
    Ipv4Only,
}

/// <summary>
/// «Защита и утечки» (доска 5) — все, кроме самого блока при обрыве: он в
/// <see cref="AppSettings.KillSwitch"/>, общий с разделом «Основные». Значения по умолчанию —
/// как в макете: защита при загрузке, блок DNS провайдера, IPv6 и локальная сеть закрыты.
/// </summary>
public sealed record ProtectionSettings(
    bool BootProtection = true,
    bool StrictMode = false,
    DnsChoice Dns = DnsChoice.Profile,
    string? CustomDns = null,
    bool BlockOutsideDns = true,
    bool FakeDns = false,
    bool CheckLeaksAfterConnect = true,
    bool BlockIpv6 = true,
    AddressPreference Addresses = AddressPreference.PreferIpv4,
    bool AllowLan = false,
    bool LanDiscovery = false)
{
    public static ProtectionSettings Default { get; } = new();

    /// <summary>Адреса DNS для службы: пусто — из профиля сервера.</summary>
    public IReadOnlyList<string> DnsServers => Dns switch
    {
        // DoH по IP-адресу: имя самого DNS-сервера не нужно разрешать мимо туннеля.
        DnsChoice.CloudflareDoh => ["https://1.1.1.1/dns-query"],
        DnsChoice.Quad9Doh => ["https://9.9.9.9/dns-query"],
        DnsChoice.Custom when CustomDns is { Length: > 0 } custom => [custom],
        _ => [],
    };
}

/// <summary>Свой адрес DNS: IP-адрес или DoH (https://…). Остальное ядра не принимают.</summary>
public static class DnsAddress
{
    /// <summary>Привести к виду для службы; null — такой адрес не подходит.</summary>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        // TryParse понимает и «1.2» как 1.0.0.2 — такие сокращения не принимаются.
        if (IPAddress.TryParse(trimmed, out var ip)
            && (ip.AddressFamily == AddressFamily.InterNetworkV6 ? trimmed.Contains(':', StringComparison.Ordinal) : trimmed.Count(c => c == '.') == 3))
        {
            return ip.ToString();
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps && url.Host.Length > 0
            ? url.GetLeftPart(UriPartial.Path)
            : null;
    }
}
