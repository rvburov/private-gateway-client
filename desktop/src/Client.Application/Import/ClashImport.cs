using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using static PrivateGateway.Client.Application.Import.ConfigImport;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Конфиг или подписка Clash/Mihomo: YAML со списком proxies. Группы, правила и провайдеры не
/// разбираются — только серверы. Служебные direct и dns пропускаются; номер строки — строка, где
/// начинается сервер.
/// </summary>
internal static class ClashImport
{
    public static ImportBatch Parse(string text)
    {
        var starts = new Dictionary<JsonNode, int>(ReferenceEqualityComparer.Instance);
        var root = YamlTree.Parse(text, starts, out var errorLine);
        if (errorLine > 0)
        {
            return Broken(ImportParser.FormatClash, errorLine);
        }

        if ((root as JsonObject)?["proxies"] is not JsonArray proxies)
        {
            return new ImportBatch(ImportParser.FormatClash, []);
        }

        var listLine = starts.GetValueOrDefault(proxies, 1);
        var lines = new List<ImportLine>();
        foreach (var item in proxies)
        {
            if (Proxy(item) is { } result)
            {
                lines.Add(new ImportLine(item is null ? listLine : starts.GetValueOrDefault(item, listLine), result));
            }
        }

        return new ImportBatch(ImportParser.FormatClash, lines);
    }

    /// <summary>Сервер из элемента proxies; null — служебный, не сервер.</summary>
    private static LinkResult? Proxy(JsonNode? node)
    {
        var type = Text(node, "type")?.ToLowerInvariant();
        var name = Text(node, "name");
        var host = Text(node, "server");
        var port = Text(node, "port");
        var query = Query();
        Put(query, "sni", Text(node, "sni") ?? Text(node, "servername"));
        Put(query, "alpn", Joined(node, "alpn"));
        Put(query, "allowInsecure", Flag(node, "skip-cert-verify") ? "1" : null);
        Put(query, "fp", Text(node, "client-fingerprint"));
        var reality = Object(node, "reality-opts");
        Put(query, "pbk", Text(reality, "public-key"));
        Put(query, "sid", Text(reality, "short-id"));
        Transport(node, query);
        switch (type)
        {
            case "direct" or "dns":
                return null;
            case "vless":
                query["security"] = Security(node, reality, "none");
                Put(query, "flow", Text(node, "flow"));
                Put(query, "encryption", Text(node, "encryption"));
                return Server(ProxyProtocol.Vless, [Text(node, "uuid")], host, port, query, name);
            case "vmess":
                query["security"] = Security(node, reality, "none");
                return LinkParser.VmessFrom(name, host, port, Text(node, "alterId"), Text(node, "cipher"), null, query, Text(node, "uuid"));
            case "trojan":
                // Trojan в Clash — всегда TLS; trojan-go с шифрованием Shadowsocks ядро не умеет.
                query["security"] = Security(node, reality, "tls");
                if (Flag(Object(node, "ss-opts"), "enabled"))
                {
                    return Fail(ImportErrors.UnsupportedParameter);
                }

                return Server(ProxyProtocol.Trojan, [Text(node, "password")], host, port, query, name);
            case "ss":
                Put(query, "plugin", Text(node, "plugin"));
                return Server(ProxyProtocol.Shadowsocks, [Text(node, "cipher"), Text(node, "password")], host, port, query, name);
            case "hysteria2":
                query["security"] = "tls";
                Put(query, "obfs", Text(node, "obfs"));
                Put(query, "obfs-password", Text(node, "obfs-password"));
                return Server(ProxyProtocol.Hysteria2, [Text(node, "password")], host, port, query, name);
            case "tuic":
                query["security"] = "tls";
                Put(query, "congestion_control", Text(node, "congestion-controller"));
                return Server(ProxyProtocol.Tuic, [Text(node, "uuid"), Text(node, "password")], host, port, query, name);
            case "wireguard":
                return WireGuard(node, name);
            case null:
                return Fail(ImportErrors.BadLink);
            default:
                return Fail(ImportErrors.UnknownScheme);
        }
    }

    /// <summary>tls: true — TLS, с reality-opts — REALITY; без tls — защита протокола по умолчанию.</summary>
    private static string Security(JsonNode? node, JsonObject? reality, string byDefault) =>
        reality is not null ? "reality" : Text(node, "tls") is null ? byDefault : Flag(node, "tls") ? "tls" : "none";

    /// <summary>network и *-opts → параметры ссылки; ws с v2ray-http-upgrade — это HTTPUpgrade.</summary>
    private static void Transport(JsonNode? node, Dictionary<string, string> query)
    {
        var network = Text(node, "network")?.ToLowerInvariant();
        var ws = Object(node, "ws-opts");
        var xhttp = Object(node, "xhttp-opts");
        Put(query, "type", network == "ws" && Flag(ws, "v2ray-http-upgrade") ? "httpupgrade" : network);
        Put(query, "path", Text(ws, "path") ?? Text(xhttp, "path"));
        Put(query, "host", Header(Object(ws, "headers"), "Host") ?? Text(xhttp, "host"));
        Put(query, "serviceName", Text(Object(node, "grpc-opts"), "grpc-service-name"));
    }

    /// <summary>WireGuard: поля сервера или один peers; AmneziaWG ядро не поддерживает.</summary>
    private static LinkResult WireGuard(JsonNode? node, string? name)
    {
        var peers = node?["peers"] as JsonArray;
        if (peers is { Count: > 1 } || Object(node, "amnezia-wg-option") is not null)
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        var peer = peers is { Count: 1 } ? peers[0] : node;
        var query = Query();
        Put(query, "publickey", Text(peer, "public-key"));
        Put(query, "presharedkey", Text(peer, "pre-shared-key"));
        Put(query, "address", string.Join(',', new[] { Text(node, "ip"), Text(node, "ipv6") }.OfType<string>()));
        Put(query, "mtu", Text(node, "mtu"));
        return Server(ProxyProtocol.WireGuard, [Text(node, "private-key")], Text(peer, "server"), Text(peer, "port"), query, name);
    }
}
