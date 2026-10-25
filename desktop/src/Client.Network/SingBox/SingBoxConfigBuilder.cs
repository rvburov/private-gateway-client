using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Network.SingBox;

/// <summary>
/// Что нужно TUN режима VPN: связь с Xray (<see cref="TunLink"/>), адреса сервера, которые идут мимо
/// туннеля, и настройки. <paramref name="DirectPrograms"/> — раздельный режим: программы, чьи
/// соединения идут мимо туннеля (<see cref="SingBoxConfigBuilder.DirectPrograms"/>); пусто — обхода нет.
/// <paramref name="FakeIpPrograms"/> — те из них, чьи соединения с адресами FakeDNS идут во вход обхода
/// Xray (<see cref="SingBoxConfigBuilder.FakeIpPrograms"/>, <see cref="TunLink.BypassPort"/>).
/// </summary>
public sealed record TunRequest(
    TunLink Xray,
    IReadOnlyList<IPAddress> ServerAddresses,
    ProtectionSettings Protection,
    ConnectionSettings Connection,
    IReadOnlyList<string>? DirectPrograms = null,
    IReadOnlyList<string>? FakeIpPrograms = null);

/// <summary>
/// Конфиг sing-box для режима VPN: только TUN. Весь трафик машины — в TUN, оттуда в SOCKS5 Xray на
/// 127.0.0.1 (основное ядро — Xray, sing-box лишь переводит пакеты в соединения). DNS — такой же
/// трафик: запросы к любому адресу уходят в Xray, и там их отвечает выход DNS по настройкам DNS Xray —
/// один DNS в режимах прокси и VPN. Адреса сервера — мимо туннеля, иначе соединение с ним ушло бы в
/// сам туннель. IPv6 закрыт — у TUN нет IPv6, строгие маршруты делают его недоступным. Раздельный
/// режим — правило по пути программы: ее соединения sing-box отправляет своим выходом напрямую, с
/// меткой <see cref="DirectMark"/> — по ней их пропускает блок при обрыве. Журнал соединений выключен.
/// sing-box — GPL-3.0: только отдельным процессом, конфиг — через stdin.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string TunTag = "tun-in";
    public const string XrayTag = "xray";
    public const string DirectTag = "direct";

    /// <summary>Выход во вход обхода Xray — для программ мимо туннеля с адресом FakeDNS.</summary>
    public const string BypassTag = "xray-bypass";

    /// <summary>Метка пакетов выхода мимо туннеля (Linux): «PG».</summary>
    public const uint DirectMark = 0x5047;

    /// <summary>Адрес TUN.</summary>
    public const string TunAddress = "172.19.0.1/30";

    public const string TunAddress6 = "fdfe:dcba:9876::1/126";

    /// <summary>Имя адаптера — по нему правила блока при обрыве узнают туннель.</summary>
    public static string InterfaceName =>
        OperatingSystem.IsWindows() ? "Private Gateway" : OperatingSystem.IsMacOS() ? string.Empty : "pgtun0";

    /// <summary>
    /// Linux: таблица маршрутов и первый номер правил (<c>ip rule</c>) туннеля — свои, а не 2022 и 9000 sing-box
    /// по умолчанию: правила, оставшиеся после аварии sing-box, служба убирает по этим номерам и не задевает
    /// такие же у другой программы на sing-box. Правила занимают номера от <see cref="RuleIndex"/> до <see cref="RuleLast"/>.
    /// </summary>
    public const int RouteTable = 20551;

    public const int RuleIndex = 9047;

    public const int RuleLast = RuleIndex + 10;

    /// <summary>Частные сети — мимо туннеля, если локальная сеть разрешена.</summary>
    private static readonly string[] PrivateNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"];

    public static JsonObject BuildTun(TunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = request.Connection;
        var protection = request.Protection;
        var ipv6 = protection.Ipv6Allowed;
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

        if (OperatingSystem.IsLinux())
        {
            tun["iproute2_table_index"] = RouteTable;
            tun["iproute2_rule_index"] = RuleIndex;
        }

        var xray = request.Xray;
        var outbounds = new JsonArray(new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = XrayTag,
            ["server"] = "127.0.0.1",
            ["server_port"] = xray.Port,
            ["version"] = "5",
            ["username"] = xray.User,
            ["password"] = xray.Password,
        });
        var route = new JsonObject
        {
            ["final"] = XrayTag,
            ["auto_detect_interface"] = true,
        };
        var rules = new JsonArray();

        // Программа мимо туннеля пришла на адрес FakeDNS (DNS за нее спросила служба ОС через туннель):
        // имя знает только Xray — он и соединит напрямую. Раньше правила «программа — напрямую».
        if (request.FakeIpPrograms is { Count: > 0 } fake && xray.BypassPort > 0)
        {
            outbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = BypassTag,
                ["server"] = "127.0.0.1",
                ["server_port"] = xray.BypassPort,
                ["version"] = "5",
                ["username"] = xray.User,
                ["password"] = xray.Password,
            });
            rules.Add(new JsonObject
            {
                ["process_path"] = new JsonArray([.. fake.Select(p => (JsonNode)p)]),
                ["ip_cidr"] = new JsonArray(XrayConfigBuilder.FakeDnsPool),
                ["outbound"] = BypassTag,
            });
        }

        if (request.DirectPrograms is { Count: > 0 } programs)
        {
            var direct = new JsonObject { ["type"] = "direct", ["tag"] = DirectTag };
            if (OperatingSystem.IsLinux())
            {
                direct["routing_mark"] = DirectMark;
            }

            outbounds.Add(direct);
            rules.Add(new JsonObject
            {
                ["process_path"] = new JsonArray([.. programs.Select(p => (JsonNode)p)]),
                ["outbound"] = DirectTag,
            });
        }

        if (rules.Count > 0)
        {
            route["rules"] = rules;
        }

        return new JsonObject
        {
            // Не ниже info: строка «sing-box started» — признак готовности туннеля (VpnTunnel). Строки ниже уровня
            // «Журнала ядра» — с адресами соединений — служба в журнал не пускает (ShownLine).
            ["log"] = new JsonObject
            {
                ["level"] = connection.CoreLog == CoreLogLevel.Debug ? "debug" : "info",
                ["timestamp"] = false,
            },
            ["inbounds"] = new JsonArray(tun),
            ["outbounds"] = outbounds,
            ["route"] = route,
        };
    }

    /// <summary>Строка вывода sing-box, после которой все его части запущены и туннель пропускает трафик.</summary>
    public const string StartedLine = "sing-box started";

    /// <summary>
    /// Пускать ли строку вывода sing-box в журнал: ее уровень (первое слово: DEBUG, INFO, WARN, ERROR…) не ниже
    /// «Журнала ядра». На info и debug sing-box пишет адреса соединений — при уровне по умолчанию (warning)
    /// они в журнал не попадают. Строка без уровня (продолжение ошибки) — пускать.
    /// </summary>
    public static bool ShownLine(string line, CoreLogLevel level)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rank = line.Split(' ', 2)[0] switch
        {
            "TRACE" or "DEBUG" => 0,
            "INFO" => 1,
            "WARN" => 2,
            "ERROR" or "FATAL" or "PANIC" => 3,
            _ => 3,
        };
        return rank >= level switch
        {
            CoreLogLevel.Debug => 0,
            CoreLogLevel.Info => 1,
            _ => 2,
        };
    }

    /// <summary>
    /// Программы мимо туннеля: в раздельном режиме — включенные программы из настроек; и сам Xray, когда у
    /// него есть прямой выход — сайты «Напрямую», разрешенная локальная сеть или программы мимо туннеля с
    /// FakeDNS. Иначе его прямое соединение вернулось бы в TUN и снова в Xray: в исключениях TUN не все, что
    /// Xray считает локальным (<c>geoip:private</c> — еще CGNAT, адреса документации, групповые). Ничего из
    /// этого нет — никого. Пути — как их видит ОС у процесса (<paramref name="xray"/> — настоящий путь, без ссылок).
    /// </summary>
    public static IReadOnlyList<string> DirectPrograms(RoutingSettings routing, ProtectionSettings protection, string xray)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(protection);
        return
        [
            .. routing.HasBypassApps ? routing.Apps.Where(a => a.IsEnabled).Select(a => a.Path) : [],
            .. routing.HasBypassSites || protection.AllowLan || FakeIpPrograms(routing, protection).Count > 0 ? [xray] : Array.Empty<string>(),
        ];
    }

    /// <summary>
    /// Программы мимо туннеля, чьи соединения с адресами FakeDNS ведет Xray (вход обхода): с FakeDNS — все
    /// включенные программы раздельного режима, без него — никто. Сам Xray сюда не входит: его соединение с
    /// адресом из пула вернулось бы в него же.
    /// </summary>
    public static IReadOnlyList<string> FakeIpPrograms(RoutingSettings routing, ProtectionSettings protection)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(protection);
        return protection.FakeDns && routing.HasBypassApps ? [.. routing.Apps.Where(a => a.IsEnabled).Select(a => a.Path)] : [];
    }
}
