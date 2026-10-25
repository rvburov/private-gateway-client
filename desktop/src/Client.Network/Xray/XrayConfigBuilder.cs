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
    /// <summary>Hysteria2 и TUIC — для второго ядра (sing-box), после технического спайка.</summary>
    UnsupportedProtocol,

    /// <summary>Нет данных доступа: UUID, пароля или ключа.</summary>
    MissingCredentials,

    /// <summary>Транспорт, которого у протокола в Xray нет (например, QUIC у VLESS).</summary>
    UnsupportedTransport,
}

/// <summary>Конфиг Xray или причина отказа.</summary>
public sealed record XrayConfigResult(JsonObject? Config, CoreRejection? Rejection)
{
    public bool IsOk => Config is not null;
}

/// <summary>
/// Связь Xray с TUN в режиме VPN: вход SOCKS5 на 127.0.0.1:<paramref name="Port"/> с паролем на это
/// подключение — другие программы машины в него не войдут; выход — к <paramref name="ServerAddress"/>,
/// адресу сервера, который служба разрешила заранее и вывела из маршрутов TUN (иначе соединение с
/// сервером ушло бы в сам туннель). Имя сервера для TLS и заголовков остается прежним.
/// </summary>
public sealed record TunLink(int Port, string User, string Password, string ServerAddress);

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
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string FragmentTag = "fragment";

    /// <summary>Выход DNS режима VPN: отвечает на запросы из TUN по настройкам DNS этого конфига.</summary>
    public const string DnsTag = "dns-out";

    /// <summary>DNS по умолчанию, если в профиле своего нет: DoH через туннель.</summary>
    public const string DefaultDns = "https://1.1.1.1/dns-query";

    /// <param name="request">Сервер и настройки.</param>
    /// <param name="tun">Режим VPN: вход только для своего TUN и адрес сервера; null — вход прокси из настроек.</param>
    public static XrayConfigResult Build(CoreRequest request, TunLink? tun = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outbound = Outbound(request.Server, request.Connection, out var rejection);
        if (outbound is null)
        {
            return new XrayConfigResult(null, rejection);
        }

        if (tun is not null)
        {
            Dial(outbound, tun.ServerAddress);
        }

        var outbounds = new JsonArray
        {
            outbound,
            new JsonObject
            {
                ["tag"] = DirectTag,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject
                {
                    ["domainStrategy"] = request.Protection.Addresses == AddressPreference.Ipv4Only ? "UseIPv4" : "AsIs",
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
            outbounds.Add(new JsonObject { ["tag"] = DnsTag, ["protocol"] = "dns" });
        }

        return new XrayConfigResult(new JsonObject
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
            ["dns"] = Dns(request.Protection),
            ["inbounds"] = new JsonArray { tun is null ? Inbound(request) : TunInbound(request, tun) },
            ["outbounds"] = outbounds,
            ["routing"] = Routing(request, tunDns: tun is not null),
        }, null);
    }

    /// <summary>
    /// Конфиг проверки задержки: на каждый сервер — свой вход SOCKS5 на 127.0.0.1 (порт из
    /// <paramref name="ports"/> с тем же номером) и свой выход, вход ведет только в свой выход. Без DNS,
    /// правил и журнала соединений — проверяется путь через сервер с настройками подключения
    /// (фрагментация, Mux, отпечаток). Сервер, за который ядро не возьмется, входа не получает.
    /// </summary>
    public static XrayProbeConfig BuildProbe(IReadOnlyList<ServerDefinition> servers, IReadOnlyList<int> ports, ConnectionSettings connection)
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
            if (Outbound(servers[i], connection, out rejections[i]) is not { } outbound)
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
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = false },
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
            ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = "100-200", ["interval"] = "10-20" },
        },
    };

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

    private static JsonObject TunInbound(CoreRequest request, TunLink tun) => new()
    {
        ["tag"] = InboundTag,
        ["listen"] = "127.0.0.1",
        ["port"] = tun.Port,
        ["protocol"] = "socks",
        ["settings"] = new JsonObject
        {
            ["auth"] = "password",
            ["accounts"] = new JsonArray(new JsonObject { ["user"] = tun.User, ["pass"] = tun.Password }),
            ["udp"] = true,
        },
        ["sniffing"] = new JsonObject
        {
            ["enabled"] = request.Connection.Sniffing,
            ["destOverride"] = new JsonArray("http", "tls", "quic"),
            ["routeOnly"] = true,
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

    private static JsonObject Dns(ProtectionSettings protection) => new()
    {
        ["servers"] = new JsonArray(protection.Dns switch
        {
            DnsChoice.CloudflareDoh => "https://1.1.1.1/dns-query",
            DnsChoice.Quad9Doh => "https://9.9.9.9/dns-query",
            DnsChoice.Custom when protection.CustomDns is { Length: > 0 } custom => custom,
            _ => DefaultDns,
        }),
        ["queryStrategy"] = protection.Addresses == AddressPreference.Ipv4Only ? "UseIPv4" : "UseIP",
    };

    private static JsonObject Routing(CoreRequest request, bool tunDns)
    {
        var rules = new JsonArray();
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

        // Локальная сеть: разрешена — напрямую, иначе — блок (по умолчанию запрещена, модель угроз).
        rules.Add(new JsonObject
        {
            ["type"] = "field",
            ["ip"] = new JsonArray("geoip:private"),
            ["outboundTag"] = request.Protection.AllowLan ? DirectTag : BlockTag,
        });
        if (request.Protection.BlockIpv6)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("::/0"), ["outboundTag"] = BlockTag });
        }

        // «Напрямую» — только в раздельном режиме: в режиме «Весь трафик» мимо VPN ничего не идет.
        var split = request.Routing.Mode == RoutingMode.Split;
        foreach (var rule in request.Routing.Rules.Where(r => split || r.Action != RuleAction.Direct))
        {
            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray(DomainCondition(rule.Pattern)),
                ["outboundTag"] = rule.Action switch
                {
                    RuleAction.Direct => DirectTag,
                    RuleAction.Block => BlockTag,
                    _ => ProxyTag,
                },
            });
        }

        return new JsonObject { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = rules };
    }

    private static JsonObject? Outbound(ServerDefinition server, ConnectionSettings connection, out CoreRejection? rejection)
    {
        rejection = null;
        var credentials = server.Credentials;
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
            case ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic:
                rejection = CoreRejection.UnsupportedProtocol;
                return null;
            default:
                rejection = CoreRejection.MissingCredentials;
                return null;
        }

        if (Stream(server, connection) is not { } stream)
        {
            rejection = CoreRejection.UnsupportedTransport;
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

        var settings = new JsonObject { ["secretKey"] = privateKey, ["peers"] = new JsonArray(peer) };
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

                if (security.AllowInsecure)
                {
                    tls["allowInsecure"] = true;
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
