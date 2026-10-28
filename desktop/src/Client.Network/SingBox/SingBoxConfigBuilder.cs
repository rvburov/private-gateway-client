using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Network.SingBox;

/// <summary>
/// Что нужно TUN режима VPN: связь с Xray (<see cref="TunLink"/>), адреса сервера, которые идут мимо
/// туннеля, настройки и IPv6 (<see cref="TunIpv6"/>). <paramref name="DirectPrograms"/> — раздельный режим: программы, чьи
/// соединения идут мимо туннеля (<see cref="SingBoxConfigBuilder.DirectPrograms"/>); пусто — обхода нет.
/// <paramref name="FakeIpPrograms"/> — те из них, чьи соединения с адресами FakeDNS идут во вход обхода
/// Xray (<see cref="SingBoxConfigBuilder.FakeIpPrograms"/>, <see cref="TunLink.BypassPort"/>).
/// <paramref name="Routes"/> — только эти сети через TUN, без строгих маршрутов: тесты Windows в CI (решение 438) —
/// туннель на весь трафик забрал бы связь машины CI с GitHub; null — весь трафик.
/// <paramref name="Subnet"/> — адреса TUN, свободные на этой машине (<see cref="TunSubnet.Choose"/>); null —
/// <see cref="TunSubnet.Default"/>.
/// </summary>
public sealed record TunRequest(
    TunLink Xray,
    IReadOnlyList<IPAddress> ServerAddresses,
    ProtectionSettings Protection,
    ConnectionSettings Connection,
    TunIpv6 Ipv6,
    IReadOnlyList<string>? DirectPrograms = null,
    IReadOnlyList<string>? FakeIpPrograms = null,
    IReadOnlyList<string>? Routes = null,
    TunSubnet? Subnet = null);

/// <summary>
/// Сервер Hysteria2 или TUIC за ретранслятором (<see cref="SingBoxConfigBuilder.BuildRelay"/>): вход для Xray и адрес, к которому
/// соединяться — разрешенный службой заранее (режим VPN: он же выведен из маршрутов TUN и открыт блоком); null — имя из описания.
/// </summary>
public sealed record RelayRoute(ServerDefinition Server, RelayLink Link, string? Address = null);

/// <summary>
/// IPv6 для TUN — решает служба: <paramref name="Kernel"/> — в ядре ОС есть IPv6 (правила маршрутизации IPv6 ставятся);
/// <paramref name="Tunnel"/> — IPv6 идет через туннель: настройки его разрешают, сервер его открывает, ОС дает адаптеру
/// адрес IPv6.
/// </summary>
public sealed record TunIpv6(bool Kernel, bool Tunnel);

/// <summary>
/// Конфиг sing-box для режима VPN: только TUN. Весь трафик машины — в TUN, оттуда в SOCKS5 Xray на
/// 127.0.0.1 (основное ядро — Xray, sing-box лишь переводит пакеты в соединения). DNS — такой же
/// трафик: запросы к любому адресу уходят в Xray, и там их отвечает выход DNS по настройкам DNS Xray —
/// один DNS в режимах прокси и VPN; DNS ОС — адрес <see cref="TunDns"/>, его sing-box прописывает ОС. Адреса сервера — мимо туннеля, иначе соединение с ним ушло бы в
/// сам туннель. IPv6 идет через туннель, если служба решила, что сервер его открывает (<see cref="TunIpv6"/>); иначе у TUN
/// нет IPv6, и строгие маршруты делают его недоступным (<see cref="StrictRoutes"/>). Раздельный
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

    /// <summary>Адрес TUN по умолчанию (<see cref="TunSubnet.Default"/>); занят на машине — служба берет другую подсеть.</summary>
    public const string TunAddress = "172.19.0.1/30";

    public const string TunAddress6 = "fdfe:dcba:9876::1/126";

    /// <summary>
    /// Адрес DNS туннеля по умолчанию — следующий за адресом TUN (<see cref="TunSubnet.Dns"/>). Его sing-box прописывает ОС
    /// (Windows — DNS адаптера, Linux — systemd-resolved), и запросы к нему идут в Xray, как к любому адресу.
    /// </summary>
    public const string TunDns = "172.19.0.2";

    public const string TunDns6 = "fdfe:dcba:9876::2";

    /// <summary>
    /// Имя адаптера — по нему правила блока при обрыве узнают туннель. На macOS адаптер — только utunN: номер свой и
    /// высокий, чтобы не совпасть со штатными utun0–utun3 и чужими VPN.
    /// </summary>
    public static string InterfaceName =>
        OperatingSystem.IsWindows() ? "Private Gateway" : OperatingSystem.IsMacOS() ? "utun225" : "pgtun0";

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

    /// <summary>IPv6 идет через туннель: настройки его разрешают, и служба решила, что сервер и ОС его дают.</summary>
    public static bool Ipv6Tunneled(TunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Ipv6.Tunnel && request.Protection.Ipv6Allowed;
    }

    /// <summary>
    /// Строгие маршруты TUN. IPv6, который не идет через туннель, закрывают только они (если блок при обрыве выключен): у
    /// TUN без адреса IPv6 sing-box ставит на все IPv6 правило «недоступно» (Linux) или фильтр «block ipv6» (Windows) — и
    /// для сокетов, привязанных к адресу. Поэтому при «Блокировать IPv6 вне туннеля» и «Только IPv4» они включены всегда.
    /// На Linux без IPv6 в ядре sing-box с ними не стартует (правило IPv6 не поставить) — и закрывать там нечего. Сервер
    /// только с IPv6 (Xray соединяется с ним по IPv6), а у TUN IPv6 нет — строгих маршрутов нет: они закрыли бы и это
    /// соединение; IPv6 программ тогда держит блок при обрыве.
    /// </summary>
    public static bool StrictRoutes(TunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var protection = request.Protection;
        var tunneled = Ipv6Tunneled(request);
        var serverOverIpv6 = IPAddress.TryParse(request.Xray.ServerAddress, out var server) && server.AddressFamily == AddressFamily.InterNetworkV6;
        return (request.Connection.StrictRoutes || (!tunneled && (protection.BlockIpv6 || !protection.Ipv6Allowed)))
            && (request.Ipv6.Kernel || !OperatingSystem.IsLinux())
            && (tunneled || !serverOverIpv6);
    }

    /// <summary>IPv6 программ идет мимо туннеля: через туннель не идет, строгих маршрутов нет, а IPv6 в ОС есть.</summary>
    public static bool Ipv6Outside(TunRequest request) => !Ipv6Tunneled(request) && !StrictRoutes(request) && request.Ipv6.Kernel;

    public static JsonObject BuildTun(TunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = request.Connection;
        var protection = request.Protection;
        var subnet = request.Subnet ?? TunSubnet.Default;
        var dns = subnet.Dns.ToString();
        var tun = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = TunTag,
            ["address"] = Ipv6Tunneled(request) ? new JsonArray(subnet.Cidr, TunAddress6) : new JsonArray(subnet.Cidr),

            // Адрес DNS ОС задан явно: без него sing-box перехватывает запросы к нему и отвечает своим DNS (системный,
            // мимо Xray и сервера). С ним это обычный трафик TUN — в Xray, как DNS к любому адресу.
            ["dns_address"] = Ipv6Tunneled(request) ? new JsonArray(dns, TunDns6) : new JsonArray(dns),
            ["auto_route"] = true,
            ["strict_route"] = StrictRoutes(request),
            ["stack"] = connection.Stack switch
            {
                TunEngine.Gvisor => "gvisor",
                TunEngine.Mixed => "mixed",
                _ => "system",
            },
            // Мимо TUN — адреса сервера, свой DNS в частной сети (его Xray спрашивает напрямую) и частные сети, если разрешены.
            ["route_exclude_address"] = new JsonArray([
                .. request.ServerAddresses.Concat(protection.LanDns is { } lanDns ? [lanDns] : [])
                    .Select(a => (JsonNode)(a.AddressFamily == AddressFamily.InterNetworkV6 ? $"{a}/128" : $"{a}/32")),
                .. protection.AllowLan ? PrivateNetworks.Select(n => (JsonNode)n) : []]),
        };
        if (OperatingSystem.IsLinux())
        {
            // Маршруты туннеля — половинами, а не одним маршрутом по умолчанию: правило sing-box «таблица туннеля без
            // маршрута по умолчанию» пропускает /0, и тогда адрес с более узким маршрутом в основной таблице (своя сеть
            // IPv6, маршрут из объявления роутера в чужом Wi-Fi) ушел бы мимо туннеля. У IPv4 таблицу дробит адрес
            // сервера, у IPv6 сервера по IPv6 может не быть.
            tun["route_address"] = Ipv6Tunneled(request)
                ? new JsonArray("0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1")
                : new JsonArray("0.0.0.0/1", "128.0.0.0/1");
        }

        if (request.Routes is { } routes)
        {
            tun["route_address"] = new JsonArray([.. routes.Select(r => (JsonNode)r)]);
            tun["strict_route"] = false;
        }

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
        // DNS к адресу DNS туннеля — всегда в Xray, и от программ мимо туннеля: адрес есть только внутри TUN, напрямую
        // через карту на него ответа нет (программа со своим DNS-клиентом берет DNS адаптера, как Chromium на Windows).
        var rules = new JsonArray(new JsonObject
        {
            ["ip_cidr"] = Ipv6Tunneled(request) ? new JsonArray(dns + "/32", TunDns6 + "/128") : new JsonArray(dns + "/32"),
            ["port"] = 53,
            ["outbound"] = XrayTag,
        });

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

        route["rules"] = rules;

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

    /// <summary>
    /// Ретранслятор Hysteria2 и TUIC за Xray (решение 452): на каждый сервер — вход SOCKS5 на 127.0.0.1 с паролем на это
    /// подключение (выход сервера в конфиге Xray, <see cref="RelayLink"/>) и выход hysteria2 или tuic к серверу; вход ведет
    /// только в свой выход. Ни DNS, ни правил, ни TUN — их держат Xray и TUN. Журнал — как у TUN: «sing-box started» —
    /// признак готовности, строки ниже уровня «Журнала ядра» служба в журнал не пускает (<see cref="ShownLine"/>).
    /// </summary>
    public static JsonObject BuildRelay(IReadOnlyList<RelayRoute> routes, CoreLogLevel log = CoreLogLevel.Warning)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0)
        {
            throw new ArgumentException("Ретранслятору нужен хотя бы один сервер", nameof(routes));
        }

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        for (var i = 0; i < routes.Count; i++)
        {
            var (server, link, address) = routes[i];
            inbounds.Add(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = $"relay-in-{i}",
                ["listen"] = "127.0.0.1",
                ["listen_port"] = link.Port,
                ["users"] = new JsonArray(new JsonObject { ["username"] = link.User, ["password"] = link.Password }),
            });
            outbounds.Add(RelayOutbound(server, address, $"relay-out-{i}"));
            rules.Add(new JsonObject { ["inbound"] = new JsonArray($"relay-in-{i}"), ["outbound"] = $"relay-out-{i}" });
        }

        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = log == CoreLogLevel.Debug ? "debug" : "info", ["timestamp"] = false },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject { ["rules"] = rules, ["final"] = "relay-out-0" },
        };
    }

    // Выход к серверу: TLS — с именем из ссылки (без него — имя сервера) и ALPN ссылки; у TUIC без ALPN — h3, как у его
    // серверов. Неизвестный алгоритм управления перегрузкой не передается — sing-box с ним не запустился бы.
    private static JsonObject RelayOutbound(ServerDefinition server, string? address, string tag)
    {
        var security = server.Security;
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = security.Sni is { Length: > 0 } sni ? sni : server.Host,
            ["insecure"] = security.AllowInsecure,
        };
        IReadOnlyList<string>? alpn = security.Alpn is { Count: > 0 } list ? list : server.Protocol == ProxyProtocol.Tuic ? ["h3"] : null;
        if (alpn is not null)
        {
            tls["alpn"] = new JsonArray([.. alpn.Select(a => (JsonNode)a)]);
        }

        var credentials = server.Credentials;
        var outbound = new JsonObject
        {
            ["type"] = server.Protocol == ProxyProtocol.Tuic ? "tuic" : "hysteria2",
            ["tag"] = tag,
            ["server"] = address ?? server.Host,
            ["server_port"] = server.Port,
        };
        if (server.Protocol == ProxyProtocol.Tuic)
        {
            outbound["uuid"] = credentials.Uuid;
            outbound["password"] = credentials.Password;
            if (server.Options.CongestionControl is "cubic" or "new_reno" or "bbr")
            {
                outbound["congestion_control"] = server.Options.CongestionControl;
            }
        }
        else
        {
            outbound["password"] = credentials.Password;
            if (server.Options.Obfs == "salamander" && credentials.ObfsPassword is { Length: > 0 } obfs)
            {
                outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = obfs };
            }
        }

        outbound["tls"] = tls;
        return outbound;
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
    /// Xray считает локальным (<see cref="Xray.XrayConfigBuilder.LocalNetworks"/> — еще CGNAT, адреса документации, групповые). Ничего из
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
