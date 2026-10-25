using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Network.SingBox;

/// <summary>
/// Что нужно TUN режима VPN: связь с Xray (<see cref="TunLink"/>), адреса сервера, которые идут мимо
/// туннеля, и настройки. <paramref name="Netns"/> — создать TUN в сетевом пространстве Linux (тесты).
/// </summary>
public sealed record TunRequest(
    TunLink Xray,
    IReadOnlyList<IPAddress> ServerAddresses,
    ProtectionSettings Protection,
    ConnectionSettings Connection,
    string? Netns = null);

/// <summary>
/// Конфиг sing-box для режима VPN: только TUN. Весь трафик машины — в TUN, оттуда в SOCKS5 Xray на
/// 127.0.0.1 (основное ядро — Xray, sing-box лишь переводит пакеты в соединения). DNS — такой же
/// трафик: запросы к любому адресу уходят в Xray, и там их отвечает выход DNS по настройкам DNS Xray —
/// один DNS в режимах прокси и VPN. Адреса сервера — мимо туннеля, иначе соединение с ним ушло бы в
/// сам туннель. IPv6 закрыт — у TUN нет IPv6, строгие маршруты делают его недоступным. Журнал
/// соединений выключен. sing-box — GPL-3.0: только отдельным процессом, конфиг — через stdin.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunTag = "tun-in";
    public const string XrayTag = "xray";

    /// <summary>Адрес TUN.</summary>
    public const string TunAddress = "172.19.0.1/30";

    public const string TunAddress6 = "fdfe:dcba:9876::1/126";

    /// <summary>Имя адаптера — по нему правила блока при обрыве узнают туннель.</summary>
    public static string InterfaceName =>
        OperatingSystem.IsWindows() ? "Private Gateway" : OperatingSystem.IsMacOS() ? string.Empty : "pgtun0";

    /// <summary>Частные сети — мимо туннеля, если локальная сеть разрешена.</summary>
    private static readonly string[] PrivateNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"];

    public static JsonObject BuildTun(TunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = request.Connection;
        var protection = request.Protection;
        var ipv6 = !protection.BlockIpv6 && protection.Addresses != AddressPreference.Ipv4Only;
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = TunTag,
            ["address"] = ipv6 ? new JsonArray(TunAddress, TunAddress6) : new JsonArray(TunAddress),
            ["auto_route"] = true,
            ["strict_route"] = connection.StrictRoutes,
            ["stack"] = connection.Stack switch
            {
                TunEngine.Gvisor => "gvisor",
                TunEngine.Mixed => "mixed",
                _ => "system",
            },
            ["route_exclude_address"] = new JsonArray([
                .. request.ServerAddresses.Select(a => (JsonNode)(a.AddressFamily == AddressFamily.InterNetworkV6 ? $"{a}/128" : $"{a}/32")),
                .. protection.AllowLan ? PrivateNetworks.Select(n => (JsonNode)n) : []]),
        };
        if (InterfaceName is { Length: > 0 } name)
        {
            tun["interface_name"] = name;
        }

        if (connection.Mtu is { } mtu)
        {
            tun["mtu"] = mtu;
        }

        if (request.Netns is { } netns)
        {
            tun["netns"] = netns;
        }

        var xray = request.Xray;
        return new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = connection.CoreLog switch
                {
                    CoreLogLevel.Debug => "debug",
                    CoreLogLevel.Info => "info",
                    _ => "warn",
                },
                ["timestamp"] = false,
            },
            ["inbounds"] = new JsonArray(tun),
            ["outbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = XrayTag,
                ["server"] = "127.0.0.1",
                ["server_port"] = xray.Port,
                ["version"] = "5",
                ["username"] = xray.User,
                ["password"] = xray.Password,
            }),
            ["route"] = new JsonObject
            {
                ["final"] = XrayTag,
                ["auto_detect_interface"] = true,
            },
        };
    }
}
