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

    /// <summary>DNS по умолчанию, если в профиле своего нет: DoH через туннель.</summary>
    public const string DefaultDns = "https://1.1.1.1/dns-query";

    public static XrayConfigResult Build(CoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outbound = Outbound(request.Server, request.Connection, out var rejection);
        if (outbound is null)
        {
            return new XrayConfigResult(null, rejection);
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
            // Фрагментация TLS ClientHello против DPI: выход «proxy» дозванивается через этот.
            outbounds.Add(new JsonObject
            {
                ["tag"] = FragmentTag,
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject
                {
                    ["fragment"] = new JsonObject { ["packets"] = "tlshello", ["length"] = "100-200", ["interval"] = "10-20" },
                },
            });
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
            ["inbounds"] = new JsonArray { Inbound(request) },
            ["outbounds"] = outbounds,
            ["routing"] = Routing(request),
        }, null);
    }

    /// <summary>Шаблон сайта из «Маршрутизации» — в условие Xray: «example.com» и «*.example.com» — домен с поддоменами.</summary>
    public static string DomainCondition(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.StartsWith("geosite:", StringComparison.Ordinal) ? pattern
            : "domain:" + (pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern);
    }

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

    private static JsonObject Routing(CoreRequest request)
    {
        var rules = new JsonArray
        {
            // Локальная сеть: разрешена — напрямую, иначе — блок (по умолчанию запрещена, модель угроз).
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = new JsonArray("geoip:private"),
                ["outboundTag"] = request.Protection.AllowLan ? DirectTag : BlockTag,
            },
        };
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
