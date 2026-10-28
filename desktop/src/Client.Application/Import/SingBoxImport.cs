using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using static PrivateGateway.Client.Application.Import.ConfigImport;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Конфиг или подписка sing-box: JSON с массивами outbounds и endpoints (WireGuard с версии 1.11)
/// или просто массив выходов. Служебные выходы (direct, block, dns, selector, urltest)
/// пропускаются; номер строки — строка, где начинается выход.
/// </summary>
internal static class SingBoxImport
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    private static readonly string[] Lists = ["outbounds", "endpoints"];

    public static ImportBatch Parse(string text)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: Options);
        }
        catch (JsonException e)
        {
            return Broken(ImportParser.FormatSingBox, (int)(e.LineNumber ?? 0) + 1);
        }

        var starts = ItemLines(text);
        var lines = new List<ImportLine>();
        foreach (var list in root is JsonArray ? [string.Empty] : Lists)
        {
            if ((list.Length == 0 ? root : (root as JsonObject)?[list]) is not JsonArray items)
            {
                continue;
            }

            var lineOf = starts.GetValueOrDefault(list) ?? [];
            for (var i = 0; i < items.Count; i++)
            {
                if (Outbound(items[i]) is { } result)
                {
                    lines.Add(new ImportLine(i < lineOf.Count ? lineOf[i] : 1, result));
                }
            }
        }

        return new ImportBatch(ImportParser.FormatSingBox, [.. lines.OrderBy(l => l.Number)]);
    }

    /// <summary>Сервер из выхода; null — служебный выход, не сервер.</summary>
    private static LinkResult? Outbound(JsonNode? node)
    {
        var type = Text(node, "type")?.ToLowerInvariant();
        var name = Text(node, "tag");
        var host = Text(node, "server");
        var port = Text(node, "server_port");
        var query = Query();
        if (type is "vless" or "vmess" or "trojan" or "hysteria2" or "tuic" && OwnCertificate(Object(node, "tls")))
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        Tls(Object(node, "tls"), query);
        Transport(Object(node, "transport"), query);
        switch (type)
        {
            case "direct" or "block" or "dns" or "selector" or "urltest":
                return null;
            case "vless":
                Put(query, "flow", Text(node, "flow"));
                return Server(ProxyProtocol.Vless, [Text(node, "uuid")], host, port, query, name);
            case "vmess":
                return LinkParser.VmessFrom(name, host, port, Text(node, "alter_id"), Text(node, "security"), null, query, Text(node, "uuid"));
            case "trojan":
                return Server(ProxyProtocol.Trojan, [Text(node, "password")], host, port, query, name);
            case "shadowsocks":
                Put(query, "plugin", Text(node, "plugin"));
                return Server(ProxyProtocol.Shadowsocks, [Text(node, "method"), Text(node, "password")], host, port, query, name);
            case "hysteria2":
                // У Hysteria2 и TUIC TLS всегда: поля tls действуют и без enabled.
                query["security"] = "tls";
                var obfs = Object(node, "obfs");
                Put(query, "obfs", Text(obfs, "type"));
                Put(query, "obfs-password", Text(obfs, "password"));
                return Server(ProxyProtocol.Hysteria2, [Text(node, "password")], host, port, query, name);
            case "tuic":
                query["security"] = "tls";
                Put(query, "congestion_control", Text(node, "congestion_control"));
                return Server(ProxyProtocol.Tuic, [Text(node, "uuid"), Text(node, "password")], host, port, query, name);
            case "wireguard":
                return WireGuard(node, name);
            case null:
                return Fail(ImportErrors.BadLink);
            default:
                return Fail(ImportErrors.UnknownScheme);
        }
    }

    /// <summary>WireGuard: выход до 1.11 (server, peer_public_key) или endpoint с одним peers.</summary>
    private static LinkResult WireGuard(JsonNode? node, string? name)
    {
        var peers = node?["peers"] as JsonArray;
        if (peers is { Count: > 1 })
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        var peer = peers is { Count: 1 } ? peers[0] : null;
        var query = Query();
        Put(query, "publickey", peer is null ? Text(node, "peer_public_key") : Text(peer, "public_key"));
        Put(query, "presharedkey", peer is null ? Text(node, "pre_shared_key") : Text(peer, "pre_shared_key"));
        Put(query, "address", Joined(node, "address") ?? Joined(node, "local_address"));
        Put(query, "mtu", Text(node, "mtu"));
        return peer is null
            ? Server(ProxyProtocol.WireGuard, [Text(node, "private_key")], Text(node, "server"), Text(node, "server_port"), query, name)
            : Server(ProxyProtocol.WireGuard, [Text(node, "private_key")], Text(peer, "address"), Text(peer, "port"), query, name);
    }

    /// <summary>tls → параметры ссылки: без tls или enabled=false — явно без защиты.</summary>
    private static void Tls(JsonObject? tls, Dictionary<string, string> query)
    {
        var reality = Object(tls, "reality");
        query["security"] = Flag(reality, "enabled") ? "reality" : Flag(tls, "enabled") ? "tls" : "none";
        Put(query, "sni", Text(tls, "server_name"));
        Put(query, "alpn", Joined(tls, "alpn"));
        Put(query, "allowInsecure", Flag(tls, "insecure") ? "1" : null);
        var utls = Object(tls, "utls");
        Put(query, "fp", Flag(utls, "enabled") ? Text(utls, "fingerprint") ?? "chrome" : null);
        Put(query, "pbk", Text(reality, "public_key"));
        Put(query, "sid", Text(reality, "short_id"));
    }

    /// <summary>
    /// Свой сертификат сервера или закрепленный ключ (certificate, certificate_path, certificate_public_key_sha256) ссылкой не передать:
    /// «pcs» — SHA-256 всего сертификата, а не PEM и не ключа. Подключить с обычной проверкой вместо закрепленной — молча ослабить
    /// защиту, поэтому такой выход — ошибка, как ссылка Hysteria2 или TUIC с закреплением (решение 483).
    /// </summary>
    private static bool OwnCertificate(JsonObject? tls) =>
        List(tls, "certificate").Count > 0 || List(tls, "certificate_path").Count > 0 || List(tls, "certificate_public_key_sha256").Count > 0;

    /// <summary>transport → параметры ссылки; «http» и «quic» разбор ссылок не принимает — так и задумано.</summary>
    private static void Transport(JsonObject? transport, Dictionary<string, string> query)
    {
        if (transport is null)
        {
            return;
        }

        Put(query, "type", Text(transport, "type"));
        Put(query, "path", Text(transport, "path"));
        Put(query, "host", Header(Object(transport, "headers"), "Host") ?? Text(transport, "host"));
        Put(query, "serviceName", Text(transport, "service_name"));
    }

    /// <summary>
    /// Строки начала элементов outbounds и endpoints (или корневого массива — ключ ""). JsonNode строк
    /// не знает, поэтому текст читается второй раз потоковым читателем.
    /// </summary>
    private static Dictionary<string, List<int>> ItemLines(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 64,
        });
        var result = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        string? property = null;
        List<int>? current = null;
        var itemDepth = 0;
        var line = 1;
        var scanned = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray when reader.CurrentDepth == 0:
                    current = result[string.Empty] = [];
                    itemDepth = 1;
                    continue;
                case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                    property = reader.GetString();
                    continue;
                case JsonTokenType.StartArray when reader.CurrentDepth == 1 && property is "outbounds" or "endpoints":
                    current = result[property!] = [];
                    itemDepth = 2;
                    continue;
                case JsonTokenType.EndArray when reader.CurrentDepth == itemDepth - 1:
                    current = null;
                    continue;
                case JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName or JsonTokenType.Comment:
                    continue;
            }

            if (current is not null && reader.CurrentDepth == itemDepth)
            {
                var start = (int)reader.TokenStartIndex;
                line += bytes.AsSpan(scanned, start - scanned).Count((byte)'\n');
                scanned = start;
                current.Add(line);
            }
        }

        return result;
    }
}
