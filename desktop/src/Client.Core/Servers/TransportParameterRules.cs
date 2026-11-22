using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateGateway.Client.Core.Servers;

/// <summary>Ограниченные параметры формата Xray: без готового конфига, команд, файлов и настроек сокета из подписки.</summary>
public sealed record TransportEndpoint(string Host, int Port);

public static class TransportParameterRules
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public const int MaxExtraBytes = 32768;

    /// <summary>Значения заголовков extra — учетные данные, в том числе у отдельной загрузки XHTTP.</summary>
    public static IReadOnlyList<string> SensitiveExtraValues(ServerDefinition server)
    {
        if (!TryXhttpExtra(server.Transport.ExtraJson, out var extra) || extra is null)
        {
            return [];
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonNode? node)
        {
            if (node is not JsonObject fields)
            {
                return;
            }

            if (fields["headers"] is JsonObject headers)
            {
                foreach (var header in headers)
                {
                    if (header.Value is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0)
                    {
                        values.Add(text);
                    }
                }
            }

            foreach (var field in fields.Where(p => p.Key != "headers"))
            {
                Visit(field.Value);
            }
        }

        Visit(JsonNode.Parse(extra));
        return [.. values];
    }

    private static readonly HashSet<string> XhttpFields = new(StringComparer.Ordinal)
    {
        "headers", "xPaddingBytes", "xPaddingObfsMode", "xPaddingKey", "xPaddingHeader", "xPaddingPlacement", "xPaddingMethod",
        "uplinkHTTPMethod", "sessionIDPlacement", "sessionIDKey", "sessionIDTable", "sessionIDLength", "seqPlacement", "seqKey",
        "uplinkDataPlacement", "uplinkDataKey", "uplinkChunkSize", "noGRPCHeader", "noSSEHeader", "scMaxEachPostBytes",
        "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs", "serverMaxHeaderBytes", "xmux", "downloadSettings",
    };

    private static readonly HashSet<string> StreamFields = new(StringComparer.Ordinal)
    {
        "address", "port", "network", "security", "tlsSettings", "realitySettings", "xhttpSettings", "splithttpSettings",
    };

    private static readonly HashSet<string> TlsFields = new(StringComparer.Ordinal)
    {
        "serverName", "fingerprint", "alpn", "pinnedPeerCertSha256", "verifyPeerCertByName", "minVersion", "maxVersion",
    };

    private static readonly HashSet<string> RealityFields = new(StringComparer.Ordinal)
    {
        "serverName", "fingerprint", "publicKey", "password", "shortId", "spiderX", "mldsa65Verify",
    };

    private static readonly HashSet<string> XmuxFields = new(StringComparer.Ordinal)
    {
        "maxConcurrency", "maxConnections", "cMaxReuseTimes", "hMaxRequestTimes", "hMaxReusableSecs", "hKeepAlivePeriod",
    };

    /// <summary>VLESS Encryption в закрепленном Xray 26.10.21; неизвестный шифр не превращается в none.</summary>
    public static bool ValidEncryption(string? text)
    {
        if (text is null or "none")
        {
            return true;
        }

        if (text.Length > 4096)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 4 or > 12 || parts[0] != "mlkem768x25519plus"
            || parts[1] is not ("native" or "xorpub" or "random") || parts[2] is not ("1rtt" or "0rtt"))
        {
            return false;
        }

        var key = false;
        foreach (var part in parts.Skip(3))
        {
            if (part.Length < 20)
            {
                if (key || part.Length == 0 || !part.All(c => char.IsAsciiDigit(c) || c == '-'))
                {
                    return false;
                }

                continue;
            }

            if (!part.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }

            try
            {
                var decoded = Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/') + new string('=', (4 - part.Length % 4) % 4));
                if (decoded.Length is not (32 or 1184))
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            key = true;
        }

        return key;
    }

    /// <summary>Разобрать extra только в известные поля XHTTP. Ограничения повторно проверяет служба на границе IPC.</summary>
    public static bool TryXhttpExtra(string? text, out string? normalized)
    {
        normalized = null;
        if (text is null)
        {
            return true;
        }

        if (Encoding.UTF8.GetByteCount(text) > MaxExtraBytes)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12, AllowDuplicateProperties = false, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!Object(doc.RootElement, XhttpFields, 0))
            {
                return false;
            }

            normalized = Canonical(doc.RootElement).ToJsonString(JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Этот профиль создает UDP-сокет к серверу; TCP-вход защищенного замера его не переносит.</summary>
    public static bool RequiresUdpProbe(ServerDefinition server)
    {
        if (server.Protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic or ProxyProtocol.WireGuard || server.Transport.Kind == TransportKind.Quic)
        {
            return true;
        }

        if (server.Transport.Kind != TransportKind.Xhttp)
        {
            return false;
        }

        if (server.Security.Kind == TransportSecurity.Tls && server.Security.Alpn is ["h3"])
        {
            return true;
        }

        if (!TryXhttpExtra(server.Transport.ExtraJson, out var extra) || extra is null)
        {
            return false;
        }

        var udp = false;
        VisitDownloads(JsonNode.Parse(extra)!.AsObject(), server.Host, server.Port, (download, _, _) =>
        {
            if (download["security"]?.GetValue<string>() == "tls"
                && download["tlsSettings"]?["alpn"] is JsonArray alpn && alpn.Count == 1 && alpn[0]?.GetValue<string>() == "h3")
            {
                udp = true;
            }
        });
        return udp;
    }

    /// <summary>Дополнительные узлы загрузки XHTTP. Пустые адрес и порт наследуют основной узел.</summary>
    public static IReadOnlyList<TransportEndpoint> DownloadEndpoints(ServerDefinition server)
    {
        var endpoints = new List<TransportEndpoint>();
        if (ExtraForServer(server) is { } extra)
        {
            VisitDownloads(JsonNode.Parse(extra)!.AsObject(), server.Host, server.Port, (download, host, port) => endpoints.Add(new(host, port)));
        }

        return [.. endpoints.Distinct()];
    }

    /// <summary>Ядру всегда передается полный адрес загрузки: Xray 26.x требует downloadSettings.address.</summary>
    public static string? ExtraForServer(ServerDefinition server)
    {
        if (!TryXhttpExtra(server.Transport.ExtraJson, out var extra))
        {
            throw new ArgumentException("Неподдерживаемые параметры XHTTP", nameof(server));
        }

        if (extra is null)
        {
            return null;
        }

        var root = JsonNode.Parse(extra)!.AsObject();
        VisitDownloads(root, server.Host, server.Port, (download, host, port) =>
        {
            download["address"] = host;
            download["port"] = port;
        });
        return root.ToJsonString(JsonOptions);
    }

    private static void VisitDownloads(JsonObject xhttp, string host, int port, Action<JsonObject, string, int> visit)
    {
        if (xhttp["downloadSettings"] is not JsonObject download)
        {
            return;
        }

        host = download["address"]?.GetValue<string>() is { Length: > 0 } address ? address : host;
        port = download["port"]?.GetValue<int>() ?? port;
        visit(download, host, port);
        foreach (var key in new[] { "xhttpSettings", "splithttpSettings" })
        {
            if (download[key] is JsonObject nested)
            {
                VisitDownloads(nested["extra"] as JsonObject ?? nested, host, port, visit);
            }
        }
    }

    private static JsonNode Canonical(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Name, Canonical(p.Value)))),
        JsonValueKind.Array => new JsonArray([.. node.EnumerateArray().Select(Canonical)]),
        _ => JsonNode.Parse(node.GetRawText())!,
    };

    private static bool Object(JsonElement node, HashSet<string> fields, int depth)
    {
        if (depth > 8 || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in node.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }

            var name = property.Name;
            var value = property.Value;
            if (!fields.Contains(name))
            {
                return false;
            }

            var valid = name switch
            {
                "downloadSettings" => Object(value, StreamFields, depth + 1),
                "xhttpSettings" or "splithttpSettings" => NestedXhttp(value, depth + 1),
                "tlsSettings" => Object(value, TlsFields, depth + 1),
                "realitySettings" => Object(value, RealityFields, depth + 1),
                "xmux" => Object(value, XmuxFields, depth + 1),
                "headers" => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(p =>
                    !p.Name.Equals("host", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String),
                "alpn" => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
                "network" => value.ValueKind == JsonValueKind.String && value.GetString() is "xhttp" or "splithttp",
                "security" => value.ValueKind == JsonValueKind.String && value.GetString() is "none" or "tls" or "reality",
                "address" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } address
                    && address.Length <= 253 && !address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\' or '@' or '#' or '?'),
                "port" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) && port is > 0 and <= 65535,
                _ => value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False
                    || value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            };
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool NestedXhttp(JsonElement node, int depth)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var fields = new HashSet<string>(XhttpFields, StringComparer.Ordinal) { "path", "host", "mode", "extra" };
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == "extra")
            {
                if (!Object(property.Value, XhttpFields, depth + 1))
                {
                    return false;
                }
            }
            else if (!Object(JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement> { [property.Name] = property.Value }), fields, depth))
            {
                return false;
            }
        }

        return true;
    }
}
