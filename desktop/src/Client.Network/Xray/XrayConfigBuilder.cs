using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Network.Xray;

/// <summary>Все, из чего служба собирает конфиг ядра: сервер и настройки из запроса Connect.</summary>
public sealed record CoreRequest(
    ServerDefinition Server,
    ConnectionMode Mode,
    ProtectionSettings Protection,
    ConnectionSettings Connection,
    RoutingSettings Routing);

/// <summary>Почему ядро Xray не возьмется за сервер.</summary>
public enum CoreRejection
{
    /// <summary>Hysteria2 и TUIC без ретранслятора sing-box (<see cref="RelayLink"/>): нет сборки sing-box.</summary>
    UnsupportedProtocol,

    /// <summary>Нет данных доступа: UUID, пароля или ключа.</summary>
    MissingCredentials,

    /// <summary>Транспорт, которого у протокола в Xray нет (например, QUIC у VLESS).</summary>
    UnsupportedTransport,

    /// <summary>
    /// VLESS или Trojan без TLS и REALITY на открытом адресе (<see cref="XrayConfigBuilder.Unencrypted"/>): трафик шел бы к
    /// серверу открытым. Xray-core с 26.x такой выход не строит («vless without TLS … is prohibited»), и у телефонов его нет.
    /// </summary>
    Unencrypted,
}

/// <summary>Конфиг Xray или причина отказа.</summary>
public sealed record XrayConfigResult(JsonObject? Config, CoreRejection? Rejection)
{
    public bool IsOk => Config is not null;
}

/// <summary>
/// Связь Xray с TUN в режиме VPN: вход Shadowsocks на 127.0.0.1:<paramref name="Port"/> с паролем на это подключение
/// (<paramref name="Password"/> — 128 случайных бит) — другие программы машины в него не войдут; UDP идет датаграммами на тот
/// же порт (решение 481). Выход — к <paramref name="ServerAddress"/>, адресу сервера, который служба разрешила заранее и
/// вывела из маршрутов TUN (иначе соединение с сервером ушло бы в сам туннель). Имя сервера для TLS и заголовков остается
/// прежним. <paramref name="BypassPort"/> — вход с тем же паролем для программ мимо туннеля, пришедших на адрес FakeDNS
/// (<see cref="XrayConfigBuilder.NeedsBypassInbound"/>): Xray узнает имя и соединяет напрямую; 0 — входа нет.
/// </summary>
public sealed record TunLink(int Port, string Password, string ServerAddress, int BypassPort = 0)
{
    /// <summary>
    /// Шифр связи: Shadowsocks AEAD с AES-128-GCM. SOCKS5 Xray-core 26.x для UDP открывает на каждый UDP ASSOCIATE свой порт и
    /// ждет датаграммы только с адреса из запроса, а sing-box пишет в запрос адрес назначения — UDP через туннель пропадал.
    /// Не Shadowsocks 2022: XUDP GlobalID (тот же внешний порт UDP у сервера после переподключения — игры, звонки) Xray дает
    /// только входам dokodemo-door, socks, shadowsocks и tun, а у входа 2022 имя другое.
    /// </summary>
    public const string Method = "aes-128-gcm";
}

/// <summary>
/// Вход проверки связи с сервером: SOCKS5 на 127.0.0.1:<paramref name="Port"/> с паролем на это
/// подключение — только для службы. Его соединения идут прямо в выход сервера, мимо правил
/// маршрутизации: проба проверяет сервер, а не правила пользователя («Напрямую» для проверочного
/// адреса или локальная сеть не обманут пробу).
/// </summary>
public sealed record ProbeLink(int Port, string User, string Password);

/// <summary>
/// Вход ретранслятора sing-box для Hysteria2 и TUIC — их Xray не знает (решение 452): SOCKS5 на 127.0.0.1:<paramref name="Port"/>
/// с паролем на это подключение. Выход сервера в конфиге Xray — к нему, а дальше sing-box соединяется с сервером сам
/// (<see cref="SingBox.SingBoxConfigBuilder.BuildRelay"/>). Маршруты, DNS, FakeDNS и правила остаются у Xray.
/// </summary>
public sealed record RelayLink(int Port, string User, string Password);

/// <summary>Имя и пароль входов замера задержки (<see cref="XrayConfigBuilder.BuildProbe"/>) — свои на каждый замер.</summary>
public sealed record SocksAccount(string User, string Password);

/// <summary>Конфиг проверки задержки и причина отказа для каждого сервера (null — сервер проверяется).</summary>
public sealed record XrayProbeConfig(JsonObject? Config, IReadOnlyList<CoreRejection?> Rejections);

/// <summary>
/// Конфиг Xray-core собирает служба сама — из описания сервера и настроек, а не из готового текста
/// (модель угроз). Вход — SOCKS5 и HTTP на одном порту (к нему же подключается TUN); выход — выбранный
/// сервер, прямой и блок. Журнал соединений выключен: адреса сайтов в журнал не попадают. Конфиг
/// содержит секреты — ядру он передается через stdin, не файлом и не аргументом.
/// </summary>
public static class XrayConfigBuilder
{
    public const string InboundTag = "socks-in";
    public const string ProbeTag = "probe-in";
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string FragmentTag = "fragment";

    /// <summary>Выход DNS режима VPN: отвечает на запросы из TUN по настройкам DNS этого конфига.</summary>
    public const string DnsTag = "dns-out";

    /// <summary>Вход программ мимо туннеля, пришедших на адрес FakeDNS (<see cref="TunLink.BypassPort"/>).</summary>
    public const string BypassTag = "bypass-in";

    /// <summary>
    /// Адреса FakeDNS: сеть для тестов производительности (RFC 2544) — в интернете ее нет, в исключения
    /// TUN и блока при обрыве она не входит.
    /// </summary>
    public const string FakeDnsPool = "198.18.0.0/15";

    /// <summary>DNS по умолчанию, если в профиле своего нет: DoH через туннель.</summary>
    public const string DefaultDns = "https://1.1.1.1/dns-query";

    /// <summary>
    /// Локальные и особые адреса — их закрывает правило локальной сети (при «Разрешить локальную сеть» — пускает напрямую):
    /// IPv4 — частные, CGNAT, петля, link-local, адреса документации и тестов, групповые, зарезервированные; IPv6 — петля,
    /// неуказанный, ULA, link-local, групповые. Это категория private геобазы Xray закрепленной версии (тот же список у
    /// v2fly/geoip и Loyalsoldier/geoip до 23.10.2026), только без самой геобазы: ее GeoIP собран из данных MaxMind
    /// GeoLite2, а нужна была от нее одна эта категория. Адреса документации IPv6 (2001:db8::/32) в ней не локальные.
    /// </summary>
    public static IReadOnlyList<string> LocalNetworks { get; } =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.88.99.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
        "255.255.255.255/32", "::/128", "::1/128", "fc00::/7", "fe80::/10", "ff00::/8",
    ];

    /// <summary>
    /// DNS самого ядра может идти напрямую: свой LAN DNS — явным правилом порта 53, а при разрешенной локальной сети
    /// ее адреса (включая числовой адрес DoH) — правилом LocalNetworks. Имя DNS при открытой LAN не доказывает публичный
    /// адрес и тоже считается возможным обходом. Использует тот же список сетей, что конфиг; сам имен не разрешает.
    /// </summary>
    public static bool DnsMayBypassServer(ProtectionSettings protection)
    {
        ArgumentNullException.ThrowIfNull(protection);
        if (protection.LanDns is not null)
        {
            return true;
        }

        if (!protection.AllowLan)
        {
            return false;
        }

        foreach (var server in protection.DnsServers)
        {
            var host = Uri.TryCreate(server, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url.Host : server;
            if (IPAddress.TryParse(host, out var address))
            {
                address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (LocalNetworks.Any(network => IPNetwork.Parse(network).Contains(address)))
                {
                    return true;
                }
            }
            else
            {
                // DoH по имени может разрешиться в LAN: загрузка только через VPN требует доказанного пути.
                return true;
            }
        }

        return false;
    }

    /// <param name="request">Сервер и настройки.</param>
    /// <param name="tun">Режим VPN: вход только для своего TUN и адрес сервера; null — вход прокси из настроек.</param>
    /// <param name="probe">Вход проверки связи с сервером; null — без него.</param>
    /// <param name="relay">Ретранслятор sing-box — у Hysteria2 и TUIC (<see cref="NeedsRelay"/>); null — нет.</param>
    public static XrayConfigResult Build(CoreRequest request, TunLink? tun = null, ProbeLink? probe = null, RelayLink? relay = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outbound = Outbound(request.Server, request.Connection, relay, out var rejection,
            tun is not null && !NeedsRelay(request.Server) ? tun.ServerAddress : null);
        if (outbound is null)
        {
            return new XrayConfigResult(null, rejection);
        }

        // Адрес сервера ретранслятор получает сам: у выхода Xray к нему — 127.0.0.1.
        if (tun is not null && !NeedsRelay(request.Server))
        {
            Dial(outbound, tun.ServerAddress);
        }

        // FakeDNS — только в режиме VPN: в режиме прокси программы отдают Xray имя сами.
        var fakeDns = tun is not null && request.Protection.FakeDns;

        var outbounds = new JsonArray
        {
            outbound,
            new JsonObject
            {
                ["tag"] = DirectTag,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject
                {
                    ["finalRules"] = AllowAll(),
                    // Имя из FakeDNS разрешает DNS этого конфига (через туннель), а не DNS ОС мимо него — с семейством по
                    // «Приоритету адресов» и запасным вторым: при UseIP Xray берет случайный адрес из A и AAAA, и без IPv6
                    // у провайдера часть соединений рвалась бы.
                    ["domainStrategy"] = request.Protection.Addresses switch
                    {
                        AddressPreference.Ipv4Only => "UseIPv4",
                        _ when !fakeDns => "AsIs",
                        AddressPreference.PreferIpv6 => "UseIPv6v4",
                        _ => "UseIPv4v6",
                    },
                },
            },
            new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" },
        };
        if (request.Connection.TlsFragment)
        {
            outbounds.Add(Fragment());
        }

        if (tun is not null)
        {
            // Запросы не A и AAAA (PTR, SRV, MX, TXT, HTTPS) — сразу отказ (REFUSED), как раньше давал «nonIPQuery: reject»: его
            // Xray-core 26.x объявил к удалению, а без правил он отвечает на такие запросы пустым NOERROR. Правилами `rules`.
            outbounds.Add(new JsonObject { ["tag"] = DnsTag, ["protocol"] = "dns", ["settings"] = new JsonObject { ["rules"] = DnsRules() } });
        }

        var inbounds = new JsonArray { tun is null ? Inbound(request) : TunInbound(request, tun, InboundTag, tun.Port) };
        var bypass = tun is { BypassPort: > 0 } && NeedsBypassInbound(request);
        if (bypass)
        {
            inbounds.Add(TunInbound(request, tun!, BypassTag, tun!.BypassPort));
        }

        if (probe is not null)
        {
            inbounds.Add(ProbeInbound(probe));
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = request.Connection.CoreLog switch
                {
                    CoreLogLevel.Debug => "debug",
                    CoreLogLevel.Info => "info",
                    _ => "warning",
                },
                ["access"] = "none",
                ["dnsLog"] = false,
            },
            ["dns"] = Dns(request, fakeDns),
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = Routing(request, tunDns: tun is not null, probe: probe is not null, bypass, fakeDns),
        };
        if (fakeDns)
        {
            config["fakedns"] = new JsonArray(new JsonObject { ["ipPool"] = FakeDnsPool, ["poolSize"] = 65535 });
        }

        return new XrayConfigResult(config, null);
    }

    /// <summary>
    /// Нужен ли вход для программ мимо туннеля: FakeDNS в режиме VPN и программы в раздельном режиме. DNS за них
    /// спрашивает служба ОС (Windows, systemd-resolved) через туннель и получает адрес FakeDNS — напрямую по нему
    /// не уйти, имя знает только Xray.
    /// </summary>
    public static bool NeedsBypassInbound(CoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Mode == ConnectionMode.Vpn && request.Protection.FakeDns && request.Routing.HasBypassApps;
    }

    /// <summary>
    /// Конфиг проверки задержки: на каждый сервер — свой вход SOCKS5 на 127.0.0.1 (порт из
    /// <paramref name="ports"/> с тем же номером) и свой выход, вход ведет только в свой выход. Без DNS,
    /// правил и журнала соединений — проверяется путь через сервер с настройками подключения
    /// (фрагментация, Mux, отпечаток). Сервер, за который ядро не возьмется, входа не получает. <paramref name="relays"/> —
    /// ретранслятор sing-box серверу с тем же номером (Hysteria2, TUIC); null — ни одного. <paramref name="account"/> — входы
    /// с этим именем и паролем: пока идет замер, другая программа на машине через них к серверам не пройдет; null — без пароля.
    /// </summary>
    public static XrayProbeConfig BuildProbe(IReadOnlyList<ServerDefinition> servers, IReadOnlyList<int> ports, ConnectionSettings connection,
        IReadOnlyList<RelayLink?>? relays = null, SocksAccount? account = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(connection);
        if (ports.Count != servers.Count)
        {
            throw new ArgumentException("Порт нужен каждому серверу", nameof(ports));
        }

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        var rejections = new CoreRejection?[servers.Count];
        for (var i = 0; i < servers.Count; i++)
        {
            if (Outbound(servers[i], connection, relays?[i], out rejections[i]) is not { } outbound)
            {
                continue;
            }

            outbound["tag"] = $"probe-out-{i}";
            outbounds.Add(outbound);
            inbounds.Add(new JsonObject
            {
                ["tag"] = $"probe-in-{i}",
                ["listen"] = "127.0.0.1",
                ["port"] = ports[i],
                ["protocol"] = "socks",
                ["settings"] = account is null
                    ? new JsonObject { ["auth"] = "noauth", ["udp"] = false }
                    : new JsonObject
                    {
                        ["auth"] = "password",
                        ["accounts"] = new JsonArray(new JsonObject { ["user"] = account.User, ["pass"] = account.Password }),
                        ["udp"] = false,
                    },
            });
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray($"probe-in-{i}"),
                ["outboundTag"] = $"probe-out-{i}",
            });
        }

        if (outbounds.Count == 0)
        {
            return new XrayProbeConfig(null, rejections);
        }

        if (connection.TlsFragment)
        {
            outbounds.Add(Fragment());
        }

        return new XrayProbeConfig(new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning", ["access"] = "none", ["dnsLog"] = false },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["rules"] = rules },
        }, rejections);
    }

    /// <summary>Шаблон сайта из «Маршрутизации» — в условие Xray: «example.com» и «*.example.com» — домен с поддоменами.</summary>
    public static string DomainCondition(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.StartsWith("geosite:", StringComparison.Ordinal) ? pattern
            : "domain:" + (pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern);
    }

    /// <summary>Фрагментация TLS ClientHello против DPI: выход к серверу дозванивается через этот.</summary>
    private static JsonObject Fragment() => new()
    {
        ["tag"] = FragmentTag,
        ["protocol"] = "freedom",
        ["settings"] = new JsonObject
        {
            ["finalRules"] = AllowAll(),
            ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = "100-200", ["interval"] = "10-20" },
        },
    };

    /// <summary>Выход DNS: A и AAAA (типы 1 и 28) отвечает сам по DNS этого конфига, остальное — отказ REFUSED (код 5).</summary>
    private static JsonArray DnsRules() => new(
        new JsonObject { ["action"] = "hijack", ["qType"] = "1,28" },
        new JsonObject { ["action"] = "return", ["rCode"] = 5 });

    /// <summary>
    /// Выходу freedom можно все: куда трафик идет, решают правила этого конфига. Xray-core 26.x у трафика входа Shadowsocks (связь с
    /// TUN) по умолчанию закрывает частные адреса — разрешенная локальная сеть и сервер в ней иначе были бы недоступны.
    /// </summary>
    private static JsonArray AllowAll() => new(new JsonObject { ["action"] = "allow" });

    private static JsonObject Inbound(CoreRequest request) => new()
    {
        ["tag"] = InboundTag,
        // Наружу — только в режиме прокси и только с явного согласия (вопрос в «Подключении и ядре»).
        ["listen"] = request.Mode == ConnectionMode.Proxy && request.Connection.ProxyAllowLan ? "0.0.0.0" : "127.0.0.1",
        ["port"] = request.Connection.ProxyPort,
        ["protocol"] = "socks",
        ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
        ["sniffing"] = new JsonObject
        {
            ["enabled"] = request.Connection.Sniffing,
            ["destOverride"] = new JsonArray("http", "tls", "quic"),
            // Домен из TLS и HTTP — только для правил, соединение идет туда, куда просили.
            ["routeOnly"] = true,
        },
    };

    private static JsonObject TunInbound(CoreRequest request, TunLink tun, string tag, int port) => new()
    {
        ["tag"] = tag,
        ["listen"] = "127.0.0.1",
        ["port"] = port,
        ["protocol"] = "shadowsocks",
        ["settings"] = new JsonObject
        {
            ["method"] = TunLink.Method,
            ["password"] = tun.Password,
            ["network"] = "tcp,udp",
        },
        ["sniffing"] = TunSniffing(request),
    };

    /// <summary>
    /// Распознавание в TUN: домен из HTTP, TLS и QUIC — только для правил; с FakeDNS — еще имя по адресу из пула,
    /// и с ним соединение идет уже к имени (Xray меняет цель для адресов FakeDNS и при routeOnly). Без
    /// распознавания HTTP и TLS FakeDNS берет имя только из адреса, не читая данных.
    /// </summary>
    private static JsonObject TunSniffing(CoreRequest request)
    {
        var sniffing = request.Connection.Sniffing;
        if (!request.Protection.FakeDns)
        {
            return new JsonObject
            {
                ["enabled"] = sniffing,
                ["destOverride"] = new JsonArray("http", "tls", "quic"),
                ["routeOnly"] = true,
            };
        }

        return new JsonObject
        {
            ["enabled"] = true,
            ["destOverride"] = sniffing ? new JsonArray("http", "tls", "quic", "fakedns") : new JsonArray("fakedns"),
            ["metadataOnly"] = !sniffing,
            ["routeOnly"] = true,
        };
    }

    private static JsonObject ProbeInbound(ProbeLink probe) => new()
    {
        ["tag"] = ProbeTag,
        ["listen"] = "127.0.0.1",
        ["port"] = probe.Port,
        ["protocol"] = "socks",
        ["settings"] = new JsonObject
        {
            ["auth"] = "password",
            ["accounts"] = new JsonArray(new JsonObject { ["user"] = probe.User, ["pass"] = probe.Password }),
            ["udp"] = false,
        },
    };

    /// <summary>Выход соединяется с уже разрешенным адресом сервера: в vnext, servers и у WireGuard — в endpoint.</summary>
    private static void Dial(JsonObject outbound, string address)
    {
        var settings = outbound["settings"]!.AsObject();
        foreach (var list in new[] { "vnext", "servers" })
        {
            foreach (var server in settings[list]?.AsArray() ?? [])
            {
                server!["address"] = address;
            }
        }

        foreach (var peer in settings["peers"]?.AsArray() ?? [])
        {
            var endpoint = peer!["endpoint"]!.GetValue<string>();
            var port = endpoint[(endpoint.LastIndexOf(':') + 1)..];
            peer["endpoint"] = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]:{port}" : $"{address}:{port}";
        }
    }

    /// <summary>
    /// DNS ядра. С FakeDNS первым идет он: запросы из TUN получают адрес из пула сразу, без запроса к DNS;
    /// собственные запросы Xray (прямой выход, правила по IP) FakeDNS пропускают и идут к настоящему DNS. Пул
    /// только IPv4 — на запросы AAAA отвечает настоящий DNS. «Только IPv4» — только адреса IPv4: на AAAA пустой ответ,
    /// программы не пробуют IPv6, а правила по адресам (IPIfNonMatch) не видят у имени адресов IPv6 — иначе правило
    /// «IPv6 — в блок» закрыло бы целиком любой сайт, у которого есть AAAA. Иначе AAAA отвечается: IPv6 идет через
    /// сервер, а в режиме VPN без IPv6 у сервера его закрывает TUN — программа сразу берет IPv4.
    /// </summary>
    private static JsonObject Dns(CoreRequest request, bool fakeDns)
    {
        var protection = request.Protection;
        JsonNode server = protection.Dns switch
        {
            DnsChoice.CloudflareDoh => "https://1.1.1.1/dns-query",
            DnsChoice.Quad9Doh => "https://9.9.9.9/dns-query",
            DnsChoice.Custom when protection.CustomDns is { Length: > 0 } custom => custom,
            _ => DefaultDns,
        };
        return new JsonObject
        {
            ["servers"] = fakeDns ? new JsonArray(RealNames(request, server), "fakedns", server.DeepClone()) : new JsonArray(server),
            ["queryStrategy"] = protection.Ipv6Allowed ? "UseIP" : "UseIPv4",
        };
    }

    /// <summary>
    /// С FakeDNS — имена, которым нужен настоящий адрес: проверка сети Windows (NCSI сверяет адрес dns.msftncsi.com, с адресом
    /// пула покажет «Нет подключения к Интернету») и сам сервер подключения. Их отвечает настоящий DNS раньше FakeDNS.
    /// </summary>
    private static JsonObject RealNames(CoreRequest request, JsonNode server)
    {
        var domains = new JsonArray("domain:msftncsi.com", "domain:msftconnecttest.com");
        var host = request.Server.Host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out _))
        {
            domains.Add("full:" + host);
        }

        return new JsonObject { ["address"] = server.DeepClone(), ["domains"] = domains, ["skipFallback"] = true };
    }

    private static JsonObject Routing(CoreRequest request, bool tunDns, bool probe, bool bypass, bool fakeDns)
    {
        var rules = new JsonArray();

        // Проба связи — первым правилом и прямо в выход сервера: ни DNS, ни правила пользователя ее не трогают.
        if (probe)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(ProbeTag), ["outboundTag"] = ProxyTag });
        }

        // Программы мимо туннеля с адресом FakeDNS — напрямую, к настоящему адресу имени.
        if (bypass)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray(BypassTag), ["outboundTag"] = DirectTag });
        }

        if (tunDns)
        {
            // DNS из TUN — к любому адресу, хоть к роутеру в локальной сети: отвечает выход DNS по
            // настройкам этого конфига. Только с входа TUN: собственные запросы DNS Xray идут через сервер.
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(InboundTag),
                ["port"] = "53",
                ["outboundTag"] = DnsTag,
            });
        }

        // Свой DNS в частной сети (Pi-hole): запросы самого Xray к нему — напрямую, и при закрытой локальной сети: через
        // сервер частный адрес не достать. Мимо TUN к нему ведет исключение в маршрутах sing-box, блок пропускает только DNS.
        if (request.Protection.LanDns is { } lanDns)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray(lanDns.ToString()),
                ["port"] = "53",
                ["outboundTag"] = DirectTag,
            });
        }

        // Локальная сеть: разрешена — напрямую, иначе — блок (по умолчанию запрещена, модель угроз).
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["ip"] = new JsonArray([.. LocalNetworks.Select(n => JsonValue.Create(n))]),
            ["outboundTag"] = request.Protection.AllowLan ? DirectTag : BlockTag,
        });
        // «Только IPv4»: адреса IPv6 без имени — в блок. Только с входа программ: собственный DNS Xray, заданный адресом
        // IPv6, идет через сервер.
        if (!request.Protection.Ipv6Allowed)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["inboundTag"] = new JsonArray(InboundTag),
                ["ip"] = new JsonArray("::/0"),
                ["outboundTag"] = BlockTag,
            });
        }

        // «Напрямую» — только в раздельном режиме: в режиме «Весь трафик» мимо VPN ничего не идет.
        var split = request.Routing.Mode == RoutingMode.Split;
        foreach (var rule in request.Routing.Rules.Where(r => split || r.Action != RuleAction.Direct))
        {
            // Адрес и подсеть — условием IP: с ним сравнивается адрес соединения, а у имени — его адреса.
            var address = SitePattern.IsAddress(rule.Pattern);
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                [address ? "ip" : "domain"] = new JsonArray(address ? rule.Pattern : DomainCondition(rule.Pattern)),
                ["outboundTag"] = rule.Action switch
                {
                    RuleAction.Direct => DirectTag,
                    RuleAction.Block => BlockTag,
                    _ => ProxyTag,
                },
            });
        }

        return new JsonObject { ["domainStrategy"] = NeedsAddressOfName(request) || !fakeDns ? "IPIfNonMatch" : "AsIs", ["rules"] = rules };
    }

    /// <summary>
    /// Нужен ли правилам адрес имени: разрешенная локальная сеть (имя с адресом в ней — напрямую) и правила для
    /// адресов и подсетей в раздельном режиме. Без них с FakeDNS Xray имя не разрешает — его разрешает сервер, и
    /// новое имя не ждет запроса к DNS через туннель. Адреса без имени правила проверяют как прежде.
    /// </summary>
    private static bool NeedsAddressOfName(CoreRequest request) =>
        request.Protection.AllowLan || request.Routing.Mode == RoutingMode.Split && request.Routing.Rules.Any(r => SitePattern.IsAddress(r.Pattern));

    /// <summary>Hysteria2 и TUIC — через ретранслятор sing-box (<see cref="RelayLink"/>): своих выходов для них у Xray нет.</summary>
    public static bool NeedsRelay(ServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic;
    }

    /// <summary>
    /// Сервер без шифрования до него: VLESS (шифрование «none») или Trojan без TLS и REALITY, а адрес, с которым соединяется
    /// Xray (<paramref name="address"/>), — не частный: ни адрес из <see cref="LocalNetworks"/>, ни имя в частной зоне (lan,
    /// local, internal, home.arpa и другие) или без точки. То же правило, что у Xray-core с 26.x
    /// (<c>validateOutboundTransportSecurity</c>): у сервера в своей сети трафик дальше нее не уходит.
    /// </summary>
    public static bool Unencrypted(ServerDefinition server, string address)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(address);
        if (server.Protocol is not (ProxyProtocol.Vless or ProxyProtocol.Trojan) || server.Security.Kind != TransportSecurity.None)
        {
            return false;
        }

        // TryParse снимает квадратные скобки IPv6, а IPNetwork считает ::ffff:a.b.c.d адресом IPv4. IPv4 — только в обычной
        // записи, как net.ParseIP у Xray: «0177.0.0.1» TryParse читает восьмеричным 127.0.0.1, а Xray — именем.
        if (IPAddress.TryParse(address, out var ip) && (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || ip.ToString() == address))
        {
            return !LocalNetworks.Any(n => IPNetwork.Parse(n).Contains(ip));
        }

        var name = address.TrimEnd('.').ToLowerInvariant();
        return !PrivateZones.Any(zone => name == zone || name.EndsWith("." + zone, StringComparison.Ordinal))
            && !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z]([a-z0-9-]{0,61}[a-z0-9])?$");
    }

    /// <summary>Частные зоны имен — те же, что у Xray-core (<c>geodata.GetPrivateDomainMatcher</c>).</summary>
    private static readonly string[] PrivateZones = ["lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal"];

    // dial — адрес, с которым соединяется выход (режим VPN — разрешенный заранее); null — адрес из описания сервера.
    private static JsonObject? Outbound(ServerDefinition server, ConnectionSettings connection, RelayLink? relay, out CoreRejection? rejection,
        string? dial = null)
    {
        rejection = null;
        var credentials = server.Credentials;
        if (NeedsRelay(server))
        {
            rejection = relay is null ? CoreRejection.UnsupportedProtocol
                : credentials.Password is not { Length: > 0 } || server.Protocol == ProxyProtocol.Tuic && credentials.Uuid is not { Length: > 0 }
                    ? CoreRejection.MissingCredentials
                    : null;
            return rejection is null ? Relay(relay!) : null;
        }

        JsonObject settings;
        switch (server.Protocol)
        {
            case ProxyProtocol.Vless when credentials.Uuid is { Length: > 0 } uuid:
                var user = new JsonObject { ["id"] = uuid, ["encryption"] = "none" };
                if (server.Options.Flow is { Length: > 0 } flow)
                {
                    user["flow"] = flow;
                }

                settings = Vnext(server, user);
                break;
            case ProxyProtocol.Vmess when credentials.Uuid is { Length: > 0 } uuid:
                settings = Vnext(server, new JsonObject { ["id"] = uuid, ["security"] = server.Options.Cipher ?? "auto" });
                break;
            case ProxyProtocol.Trojan when credentials.Password is { Length: > 0 } password:
                settings = Servers(new JsonObject { ["address"] = server.Host, ["port"] = server.Port, ["password"] = password });
                break;
            case ProxyProtocol.Shadowsocks when credentials.Password is { Length: > 0 } password && server.Options.Cipher is { Length: > 0 } method:
                settings = Servers(new JsonObject
                {
                    ["address"] = server.Host, ["port"] = server.Port, ["method"] = method, ["password"] = password,
                });
                break;
            case ProxyProtocol.WireGuard when credentials.PrivateKey is { Length: > 0 } key && server.Options.PeerPublicKey is { Length: > 0 } peerKey:
                return WireGuard(server, key, peerKey);
            default:
                rejection = CoreRejection.MissingCredentials;
                return null;
        }

        if (Stream(server, connection) is not { } stream)
        {
            rejection = CoreRejection.UnsupportedTransport;
            return null;
        }

        if (Unencrypted(server, dial ?? server.Host))
        {
            rejection = CoreRejection.Unencrypted;
            return null;
        }

        var outbound = new JsonObject
        {
            ["tag"] = ProxyTag,
            ["protocol"] = server.Protocol switch
            {
                ProxyProtocol.Vless => "vless",
                ProxyProtocol.Vmess => "vmess",
                ProxyProtocol.Trojan => "trojan",
                _ => "shadowsocks",
            },
            ["settings"] = settings,
            ["streamSettings"] = stream,
        };

        // Mux несовместим с XTLS Vision (flow) — с ним не включается.
        if (connection.Mux && string.IsNullOrEmpty(server.Options.Flow))
        {
            outbound["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = 8 };
        }

        return outbound;
    }

    // Выход к ретранслятору: SOCKS5 с паролем, UDP тоже — через UDP ASSOCIATE.
    private static JsonObject Relay(RelayLink relay) => new()
    {
        ["tag"] = ProxyTag,
        ["protocol"] = "socks",
        ["settings"] = Servers(new JsonObject
        {
            ["address"] = "127.0.0.1",
            ["port"] = relay.Port,
            ["users"] = new JsonArray(new JsonObject { ["user"] = relay.User, ["pass"] = relay.Password }),
        }),
    };

    private static JsonObject Vnext(ServerDefinition server, JsonObject user) => new()
    {
        ["vnext"] = new JsonArray(new JsonObject { ["address"] = server.Host, ["port"] = server.Port, ["users"] = new JsonArray(user) }),
    };

    private static JsonObject Servers(JsonObject server) => new() { ["servers"] = new JsonArray(server) };

    private static JsonObject WireGuard(ServerDefinition server, string privateKey, string peerKey)
    {
        var peer = new JsonObject { ["publicKey"] = peerKey, ["endpoint"] = Endpoint(server) };
        if (server.Credentials.PresharedKey is { Length: > 0 } preshared)
        {
            peer["preSharedKey"] = preshared;
        }

        // Свой стек TCP/IP Xray, а не адаптер wgN ОС: служба работает от root, и с CAP_NET_ADMIN Xray поднял бы в системе
        // свой адаптер с таблицами и правилами маршрутов — мимо TUN, и трафик через него блок при обрыве не пропускает.
        // Так же у телефонов: там прав на адаптер нет вовсе.
        var settings = new JsonObject { ["secretKey"] = privateKey, ["peers"] = new JsonArray(peer), ["noKernelTun"] = true };
        if (server.Options.Addresses is { Count: > 0 } addresses)
        {
            settings["address"] = new JsonArray([.. addresses.Select(a => JsonValue.Create(a))]);
        }

        if (server.Options.Mtu is { } mtu)
        {
            settings["mtu"] = mtu;
        }

        return new JsonObject { ["tag"] = ProxyTag, ["protocol"] = "wireguard", ["settings"] = settings };
    }

    private static string Endpoint(ServerDefinition server) =>
        server.Host.Contains(':', StringComparison.Ordinal) ? $"[{server.Host}]:{server.Port}" : $"{server.Host}:{server.Port}";

    private static JsonObject? Stream(ServerDefinition server, ConnectionSettings connection)
    {
        var transport = server.Transport;
        var network = transport.Kind switch
        {
            TransportKind.Tcp => "raw",
            TransportKind.WebSocket => "ws",
            TransportKind.Grpc => "grpc",
            TransportKind.HttpUpgrade => "httpupgrade",
            TransportKind.Xhttp => "xhttp",
            _ => null,
        };
        if (network is null)
        {
            return null;
        }

        var stream = new JsonObject { ["network"] = network };
        switch (transport.Kind)
        {
            case TransportKind.WebSocket or TransportKind.HttpUpgrade or TransportKind.Xhttp:
                var path = new JsonObject { ["path"] = transport.Path ?? "/" };
                if (transport.Host is { Length: > 0 } host)
                {
                    path["host"] = host;
                }

                stream[network + "Settings"] = path;
                break;
            case TransportKind.Grpc:
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = transport.ServiceName ?? string.Empty };
                break;
        }

        var security = server.Security;
        var fingerprint = connection.Fingerprint == TlsFingerprint.Profile
            ? security.Fingerprint
            : connection.Fingerprint.ToString().ToLowerInvariant();
        switch (security.Kind)
        {
            case TransportSecurity.Tls:
                var tls = new JsonObject { ["serverName"] = security.Sni ?? server.Host };
                if (fingerprint is { Length: > 0 })
                {
                    tls["fingerprint"] = fingerprint;
                }

                if (security.Alpn is { Count: > 0 } alpn)
                {
                    tls["alpn"] = new JsonArray([.. alpn.Select(a => JsonValue.Create(a))]);
                }

                // Сертификат проверяется всегда: allowInsecure Xray-core 26.x не принимает (конфиг с ним не запускается), а
                // самоподписанный сертификат сервера закрепляется в ссылке — «pcs» и «vcn» (решение 480).
                if (security.PinnedPeerCertSha256 is { Count: > 0 } pinned)
                {
                    tls["pinnedPeerCertSha256"] = string.Join(',', pinned);
                }

                if (security.VerifyPeerCertByName is { Count: > 0 } names)
                {
                    tls["verifyPeerCertByName"] = string.Join(',', names);
                }

                stream["security"] = "tls";
                stream["tlsSettings"] = tls;
                break;
            case TransportSecurity.Reality:
                stream["security"] = "reality";
                stream["realitySettings"] = new JsonObject
                {
                    ["serverName"] = security.Sni ?? string.Empty,
                    // REALITY без отпечатка не работает: из профиля или настройки, иначе — chrome.
                    ["fingerprint"] = fingerprint is { Length: > 0 } ? fingerprint : "chrome",
                    ["publicKey"] = security.RealityPublicKey ?? string.Empty,
                    ["shortId"] = security.RealityShortId ?? string.Empty,
                    ["spiderX"] = security.RealitySpiderX ?? string.Empty,
                };
                break;
            default:
                stream["security"] = "none";
                break;
        }

        if (connection.TlsFragment)
        {
            stream["sockopt"] = new JsonObject { ["dialerProxy"] = FragmentTag };
        }

        return stream;
    }
}
