using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Ссылка из описания сервера — для «Поделиться» (QR-код и текст). Обратная к <see cref="LinkParser"/>:
/// разбор записанной ссылки дает тот же сервер (тест — на всех эталонах shared/testdata). В ссылке —
/// секрет сервера: показывается только по явному действию пользователя.
/// </summary>
public static class LinkWriter
{
    public static string Write(ServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var credentials = server.Credentials;
        var address = (server.Host.Contains(':', StringComparison.Ordinal) ? $"[{server.Host}]" : server.Host)
            + ":" + server.Port.ToString(CultureInfo.InvariantCulture);
        var name = "#" + Escape(NameWithFlag(server));
        switch (server.Protocol)
        {
            case ProxyProtocol.Vless:
                var vless = Stream(server);
                vless.Insert(0, ("encryption", "none"));
                vless.Insert(1, ("flow", server.Options.Flow));
                return $"vless://{Escape(credentials.Uuid)}@{address}{Query(vless)}{name}";
            case ProxyProtocol.Vmess:
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Vmess(server).ToJsonString()));
            case ProxyProtocol.Trojan:
                return $"trojan://{Escape(credentials.Password)}@{address}{Query(Stream(server))}{name}";
            case ProxyProtocol.Shadowsocks:
                var method = server.Options.Cipher ?? string.Empty;
                // SIP002: обычные шифры — base64url(метод:пароль), Shadowsocks 2022 — открытым текстом.
                var userInfo = method.StartsWith("2022-", StringComparison.Ordinal)
                    ? Escape(method) + ":" + Escape(credentials.Password)
                    : Convert.ToBase64String(Encoding.UTF8.GetBytes(method + ":" + credentials.Password)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return $"ss://{userInfo}@{address}{name}";
            case ProxyProtocol.Hysteria2:
                var hysteria = Tls(server);
                hysteria.Add(("obfs", server.Options.Obfs));
                hysteria.Add(("obfs-password", credentials.ObfsPassword));
                return $"hysteria2://{Escape(credentials.Password)}@{address}{Query(hysteria)}{name}";
            case ProxyProtocol.Tuic:
                var tuic = Tls(server);
                tuic.Add(("congestion_control", server.Options.CongestionControl));
                return $"tuic://{Escape(credentials.Uuid)}:{Escape(credentials.Password)}@{address}{Query(tuic)}{name}";
            case ProxyProtocol.WireGuard:
                List<(string, string?)> wireguard =
                [
                    ("publickey", server.Options.PeerPublicKey),
                    ("presharedkey", credentials.PresharedKey),
                    ("address", server.Options.Addresses is { Count: > 0 } addresses ? string.Join(',', addresses) : null),
                    ("mtu", server.Options.Mtu?.ToString(CultureInfo.InvariantCulture)),
                ];
                return $"wireguard://{Escape(credentials.PrivateKey)}@{address}{Query(wireguard)}{name}";
            default:
                throw new ArgumentOutOfRangeException(nameof(server), server.Protocol, null);
        }
    }

    /// <summary>Название с флагом страны впереди — так его снова разберет <see cref="CountryFlag"/>.</summary>
    private static string NameWithFlag(ServerDefinition server)
    {
        if (server.CountryCode is not { Length: 2 } code || !code.All(char.IsAsciiLetterUpper))
        {
            return server.Name;
        }

        var flag = char.ConvertFromUtf32(0x1F1E6 + code[0] - 'A') + char.ConvertFromUtf32(0x1F1E6 + code[1] - 'A');
        return server.Name.Length == 0 ? flag : flag + " " + server.Name;
    }

    /// <summary>Транспорт и защита — параметры ссылки VLESS и Trojan.</summary>
    private static List<(string Key, string? Value)> Stream(ServerDefinition server)
    {
        var transport = server.Transport;
        List<(string, string?)> query =
        [
            ("type", transport.Kind switch
            {
                TransportKind.WebSocket => "ws",
                TransportKind.Grpc => "grpc",
                TransportKind.HttpUpgrade => "httpupgrade",
                TransportKind.Xhttp => "xhttp",
                _ => "tcp",
            }),
            ("path", transport.Path),
            ("host", transport.Host),
            ("serviceName", transport.ServiceName),
            ("mode", transport.Mode),
            ("security", server.Security.Kind.ToString().ToLowerInvariant()),
        ];
        query.AddRange(Tls(server));
        return query;
    }

    /// <summary>Параметры TLS и REALITY; у Hysteria2 и TUIC защита — всегда TLS, ее имя не пишется.</summary>
    private static List<(string Key, string? Value)> Tls(ServerDefinition server)
    {
        var security = server.Security;
        return
        [
            ("sni", security.Sni),
            ("fp", security.Fingerprint),
            ("alpn", security.Alpn is { Count: > 0 } alpn ? string.Join(',', alpn) : null),
            ("allowInsecure", security.AllowInsecure ? "1" : null),
            ("pcs", security.PinnedPeerCertSha256 is { Count: > 0 } pinned ? string.Join(',', pinned) : null),
            ("vcn", security.VerifyPeerCertByName is { Count: > 0 } names ? string.Join(',', names) : null),
            ("pbk", security.RealityPublicKey),
            ("sid", security.RealityShortId),
            ("spx", security.RealitySpiderX),
        ];
    }

    /// <summary>vmess:// — base64 JSON в виде v2rayN: поля, которые читает <see cref="LinkParser"/>.</summary>
    private static JsonObject Vmess(ServerDefinition server)
    {
        var transport = server.Transport;
        var security = server.Security;
        var json = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = NameWithFlag(server),
            ["add"] = server.Host,
            ["port"] = server.Port.ToString(CultureInfo.InvariantCulture),
            ["id"] = server.Credentials.Uuid,
            ["aid"] = "0",
            ["scy"] = server.Options.Cipher ?? "auto",
            ["net"] = transport.Kind switch
            {
                TransportKind.WebSocket => "ws",
                TransportKind.Grpc => "grpc",
                TransportKind.HttpUpgrade => "httpupgrade",
                TransportKind.Xhttp => "xhttp",
                _ => "tcp",
            },
            ["type"] = "none",
            ["host"] = transport.Host ?? string.Empty,
            // В vmess-ссылке имя сервиса gRPC лежит в path.
            ["path"] = (transport.Kind == TransportKind.Grpc ? transport.ServiceName : transport.Path) ?? string.Empty,
            ["tls"] = security.Kind == TransportSecurity.None ? string.Empty : security.Kind.ToString().ToLowerInvariant(),
            ["sni"] = security.Sni ?? string.Empty,
            ["fp"] = security.Fingerprint ?? string.Empty,
            ["alpn"] = security.Alpn is { Count: > 0 } alpn ? string.Join(',', alpn) : string.Empty,
        };
        // Закрепленный сертификат (решение 480) — только когда он есть: у v2rayN таких полей нет.
        if (security.PinnedPeerCertSha256 is { Count: > 0 } pinned)
        {
            json["pcs"] = string.Join(',', pinned);
        }

        if (security.VerifyPeerCertByName is { Count: > 0 } names)
        {
            json["vcn"] = string.Join(',', names);
        }

        return json;
    }

    private static string Query(List<(string Key, string? Value)> parameters)
    {
        var pairs = parameters.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => p.Key + "=" + Escape(p.Value)).ToList();
        return pairs.Count == 0 ? string.Empty : "?" + string.Join('&', pairs);
    }

    private static string Escape(string? text) => Uri.EscapeDataString(text ?? string.Empty);
}
