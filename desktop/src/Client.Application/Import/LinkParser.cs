using System.Globalization;
using System.Text;
using System.Text.Json;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Коды ошибок разбора. Одинаковые на всех платформах: эталоны в shared/testdata проверяют их
/// и на десктопе, и на телефонах.
/// </summary>
public static class ImportErrors
{
    /// <summary>Строка не похожа на ссылку: нет «схема://».</summary>
    public const string BadLink = "bad_link";

    /// <summary>Протокол ссылки программа не знает.</summary>
    public const string UnknownScheme = "unknown_scheme";

    public const string BadHost = "bad_host";

    /// <summary>Порт не число, вне 1–65535 или диапазон портов (не поддерживается).</summary>
    public const string BadPort = "bad_port";

    /// <summary>Нет UUID, пароля или ключа — подключиться нечем.</summary>
    public const string NoCredentials = "no_credentials";

    public const string BadEncoding = "bad_encoding";

    public const string UnsupportedTransport = "unsupported_transport";

    public const string UnsupportedMethod = "unsupported_method";

    /// <summary>Параметр, без которого сервер не заработает так, как задумано (plugin, alterId…).</summary>
    public const string UnsupportedParameter = "unsupported_parameter";
}

/// <summary>Результат разбора одной ссылки: сервер или код ошибки из <see cref="ImportErrors"/>.</summary>
public sealed record LinkResult(ServerDefinition? Server, string? Error)
{
    public bool IsOk => Server is not null;
}

/// <summary>
/// Разбор ссылок vless://, vmess://, trojan://, ss://, hysteria2:// (hy2://), tuic://,
/// wireguard:// (wg://). Ссылка разбирается вручную, без System.Uri: так ее одинаково
/// разбирают все платформы, а base64 и «+» в данных не портятся. Неизвестные параметры
/// пропускаются; параметры, без которых сервер работал бы не так, — ошибка.
/// </summary>
public static class LinkParser
{
    internal static readonly HashSet<string> ShadowsocksMethods = new(StringComparer.Ordinal)
    {
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
    };

    internal static readonly HashSet<string> VmessCiphers = new(StringComparer.Ordinal)
    {
        "auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero",
    };

    public static LinkResult Parse(string link)
    {
        var text = (link ?? string.Empty).Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return Fail(ImportErrors.BadLink);
        }

        var rest = text[(schemeEnd + 3)..];
        ProxyProtocol? protocol = text[..schemeEnd].ToLowerInvariant() switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "ss" => ProxyProtocol.Shadowsocks,
            "hysteria2" or "hy2" => ProxyProtocol.Hysteria2,
            "tuic" => ProxyProtocol.Tuic,
            "wireguard" or "wg" => ProxyProtocol.WireGuard,
            _ => null,
        };
        if (protocol is not { } known)
        {
            return Fail(ImportErrors.UnknownScheme);
        }

        if (known == ProxyProtocol.Vmess)
        {
            return Vmess(rest);
        }

        return Split(rest) is { } parts ? FromParts(known, parts) : Fail(ImportErrors.BadLink);
    }

    /// <summary>
    /// Сервер из частей ссылки — общий путь для ссылок и для форматов sing-box и Clash: те же
    /// проверки и те же коды ошибок. VMess — через <see cref="VmessFrom"/>.
    /// </summary>
    internal static LinkResult FromParts(ProxyProtocol protocol, LinkParts parts) => protocol switch
    {
        ProxyProtocol.Vless => Vless(parts),
        ProxyProtocol.Trojan => Trojan(parts),
        ProxyProtocol.Shadowsocks => Shadowsocks(parts),
        ProxyProtocol.Hysteria2 => Hysteria2(parts),
        ProxyProtocol.Tuic => Tuic(parts),
        ProxyProtocol.WireGuard => WireGuard(parts),
        _ => Fail(ImportErrors.UnknownScheme),
    };

    /// <summary>
    /// Части ссылки из полей конфига: секреты (через «:», каждый экранируется как в ссылке), адрес и
    /// порт (IPv6 — и без скобок: порт отделяется по последнему двоеточию), параметры и название —
    /// как после разбора ссылки.
    /// </summary>
    internal static LinkParts Parts(IEnumerable<string?> secrets, string? host, string? port, Dictionary<string, string> query, string? name) =>
        new(
            string.Join(':', secrets.Select(s => Uri.EscapeDataString(s ?? string.Empty))),
            host + ":" + port,
            query,
            (name ?? string.Empty).Trim());

    // ── Протоколы ────────────────────────────────────────────────────────────

    private static LinkResult Vless(LinkParts parts)
    {
        if (FirstError(
                Endpoint(parts, out var host, out var port),
                Transport(parts.Query, out var transport),
                Security(parts.Query, TransportSecurity.None, out var security)) is { } error)
        {
            return Fail(error);
        }

        if (Query(parts.Query, "encryption") is { } encryption && encryption != "none")
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        var uuid = Unescape(parts.UserInfo);
        return uuid.Length == 0
            ? Fail(ImportErrors.NoCredentials)
            : Ok(parts.Name, ProxyProtocol.Vless, host, port, transport, security,
                ProtocolOptions.Empty with { Flow = Query(parts.Query, "flow") },
                new ServerCredentials(Uuid: uuid));
    }

    private static LinkResult Vmess(string rest)
    {
        var body = rest.Split('#')[0].Trim();
        if (DecodeBase64(body) is not { } json)
        {
            return Fail(ImportErrors.BadEncoding);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Fail(ImportErrors.BadEncoding);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Fail(ImportErrors.BadEncoding);
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = Field(root, "net") ?? "tcp",
            ["path"] = Field(root, "path") ?? string.Empty,
            ["host"] = Field(root, "host") ?? string.Empty,
            ["security"] = Field(root, "tls") ?? string.Empty,
            ["sni"] = Field(root, "sni") ?? string.Empty,
            ["fp"] = Field(root, "fp") ?? string.Empty,
            ["alpn"] = Field(root, "alpn") ?? string.Empty,
            ["pcs"] = Field(root, "pcs") ?? string.Empty,
            ["vcn"] = Field(root, "vcn") ?? string.Empty,
        };
        // В vmess-ссылке имя сервиса gRPC лежит в path.
        query["serviceName"] = query["path"];
        return VmessFrom(Field(root, "ps"), Field(root, "add"), Field(root, "port"), Field(root, "aid"), Field(root, "scy"),
            Field(root, "type"), query, Field(root, "id"));
    }

    /// <summary>VMess из полей: alterId, шифр и тип заголовка проверяются так же, как в vmess-ссылке.</summary>
    internal static LinkResult VmessFrom(
        string? name, string? host, string? portText, string? alterId, string? cipherText, string? header,
        Dictionary<string, string> query, string? uuid)
    {
        host ??= string.Empty;
        if (!IsHost(host))
        {
            return Fail(ImportErrors.BadHost);
        }

        if (!TryPort(portText, out var port))
        {
            return Fail(ImportErrors.BadPort);
        }

        // alterId больше нуля — старый VMess без AEAD, ядро его не поддерживает.
        if (alterId is { Length: > 0 } && alterId != "0")
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        var cipher = (cipherText ?? "auto").ToLowerInvariant();
        if (!VmessCiphers.Contains(cipher))
        {
            return Fail(ImportErrors.UnsupportedMethod);
        }

        if (header is not (null or "none" or ""))
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        if (FirstError(Transport(query, out var transport), Security(query, TransportSecurity.None, out var security)) is { } error)
        {
            return Fail(error);
        }

        return string.IsNullOrEmpty(uuid)
            ? Fail(ImportErrors.NoCredentials)
            : Ok(name ?? string.Empty, ProxyProtocol.Vmess, host, port, transport, security,
                ProtocolOptions.Empty with { Cipher = cipher },
                new ServerCredentials(Uuid: uuid));
    }

    private static LinkResult Trojan(LinkParts parts)
    {
        // Trojan без TLS не работает — защита по умолчанию TLS.
        if (FirstError(
                Endpoint(parts, out var host, out var port),
                Transport(parts.Query, out var transport),
                Security(parts.Query, TransportSecurity.Tls, out var security)) is { } error)
        {
            return Fail(error);
        }

        var password = Unescape(parts.UserInfo);
        return password.Length == 0
            ? Fail(ImportErrors.NoCredentials)
            : Ok(parts.Name, ProxyProtocol.Trojan, host, port, transport, security, ProtocolOptions.Empty,
                new ServerCredentials(Password: password));
    }

    private static LinkResult Shadowsocks(LinkParts parts)
    {
        // Старый вид: ss://base64(метод:пароль@адрес:порт)#имя.
        if (parts.UserInfo.Length == 0)
        {
            if (DecodeBase64(parts.Authority) is not { } legacy || !legacy.Contains('@', StringComparison.Ordinal))
            {
                return Fail(ImportErrors.BadEncoding);
            }

            var at = legacy.LastIndexOf('@');
            parts = parts with { UserInfo = legacy[..at], Authority = legacy[(at + 1)..] };
        }

        if (Endpoint(parts, out var host, out var port) is { } error)
        {
            return Fail(error);
        }

        if (Query(parts.Query, "plugin") is not null)
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        // SIP002: base64url(метод:пароль) или метод:пароль в процентной кодировке (Shadowsocks 2022).
        var userInfo = Unescape(parts.UserInfo);
        if (!userInfo.Contains(':', StringComparison.Ordinal))
        {
            userInfo = DecodeBase64(userInfo) ?? string.Empty;
        }

        var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == userInfo.Length - 1)
        {
            return Fail(ImportErrors.NoCredentials);
        }

        var method = userInfo[..colon].ToLowerInvariant();
        return !ShadowsocksMethods.Contains(method)
            ? Fail(ImportErrors.UnsupportedMethod)
            : Ok(parts.Name, ProxyProtocol.Shadowsocks, host, port, TransportOptions.Tcp, SecurityOptions.None,
                ProtocolOptions.Empty with { Cipher = method },
                new ServerCredentials(Password: userInfo[(colon + 1)..]));
    }

    private static LinkResult Hysteria2(LinkParts parts)
    {
        if (FirstError(Endpoint(parts, out var host, out var port), Security(parts.Query, TransportSecurity.Tls, out var security)) is { } error)
        {
            return Fail(error);
        }

        var obfs = Query(parts.Query, "obfs");
        var obfsPassword = Query(parts.Query, "obfs-password");
        if (obfs is not (null or "salamander") || obfs is not null && obfsPassword is null)
        {
            return Fail(ImportErrors.UnsupportedParameter);
        }

        var password = Unescape(parts.UserInfo);
        return password.Length == 0
            ? Fail(ImportErrors.NoCredentials)
            : Ok(parts.Name, ProxyProtocol.Hysteria2, host, port, TransportOptions.Quic, security with { Kind = TransportSecurity.Tls },
                ProtocolOptions.Empty with { Obfs = obfs },
                new ServerCredentials(Password: password, ObfsPassword: obfsPassword));
    }

    private static LinkResult Tuic(LinkParts parts)
    {
        if (FirstError(Endpoint(parts, out var host, out var port), Security(parts.Query, TransportSecurity.Tls, out var security)) is { } error)
        {
            return Fail(error);
        }

        var userInfo = parts.UserInfo;
        var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        var uuid = colon > 0 ? Unescape(userInfo[..colon]) : string.Empty;
        var password = colon > 0 ? Unescape(userInfo[(colon + 1)..]) : string.Empty;
        return uuid.Length == 0 || password.Length == 0
            ? Fail(ImportErrors.NoCredentials)
            : Ok(parts.Name, ProxyProtocol.Tuic, host, port, TransportOptions.Quic, security with { Kind = TransportSecurity.Tls },
                ProtocolOptions.Empty with { CongestionControl = Query(parts.Query, "congestion_control") },
                new ServerCredentials(Uuid: uuid, Password: password));
    }

    private static LinkResult WireGuard(LinkParts parts)
    {
        if (Endpoint(parts, out var host, out var port) is { } error)
        {
            return Fail(error);
        }

        var privateKey = Unescape(parts.UserInfo);
        var peerKey = Query(parts.Query, "publickey") ?? Query(parts.Query, "public_key") ?? Query(parts.Query, "peer_public_key");
        if (privateKey.Length == 0 || peerKey is null)
        {
            return Fail(ImportErrors.NoCredentials);
        }

        var addresses = (Query(parts.Query, "address") ?? Query(parts.Query, "ip"))?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int? mtu = null;
        if (Query(parts.Query, "mtu") is { } mtuText)
        {
            if (!int.TryParse(mtuText, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 576 or > 65535)
            {
                return Fail(ImportErrors.UnsupportedParameter);
            }

            mtu = value;
        }

        return Ok(parts.Name, ProxyProtocol.WireGuard, host, port, TransportOptions.Udp, SecurityOptions.None,
            ProtocolOptions.Empty with { PeerPublicKey = peerKey, Addresses = addresses, Mtu = mtu },
            new ServerCredentials(
                PrivateKey: privateKey,
                PresharedKey: Query(parts.Query, "presharedkey") ?? Query(parts.Query, "preshared_key")));
    }

    // ── Общие части ссылки ───────────────────────────────────────────────────

    /// <summary>Части ссылки «данные@адрес:порт?параметры#имя»; имя и параметры уже раскодированы.</summary>
    internal sealed record LinkParts(string UserInfo, string Authority, Dictionary<string, string> Query, string Name);

    private static LinkParts? Split(string rest)
    {
        var name = string.Empty;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            name = Unescape(rest[(hash + 1)..]).Trim();
            rest = rest[..hash];
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var question = rest.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            foreach (var pair in rest[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = Unescape(eq >= 0 ? pair[..eq] : pair);
                var value = eq >= 0 ? Unescape(pair[(eq + 1)..]) : string.Empty;
                query.TryAdd(key, value);
            }

            rest = rest[..question];
        }

        rest = rest.TrimEnd('/');
        var at = rest.LastIndexOf('@');
        return rest.Length == 0
            ? null
            : new LinkParts(at >= 0 ? rest[..at] : string.Empty, at >= 0 ? rest[(at + 1)..] : rest, query, name);
    }

    /// <summary>Адрес и порт из «адрес:порт» или «[IPv6]:порт».</summary>
    private static string? Endpoint(LinkParts parts, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var authority = parts.Authority;
        string portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= authority.Length || authority[close + 1] != ':')
            {
                return ImportErrors.BadHost;
            }

            host = authority[1..close];
            portText = authority[(close + 2)..];
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon <= 0)
            {
                return colon == 0 ? ImportErrors.BadHost : ImportErrors.BadPort;
            }

            host = authority[..colon];
            portText = authority[(colon + 1)..];
        }

        if (!IsHost(host))
        {
            return ImportErrors.BadHost;
        }

        return TryPort(portText, out port) ? null : ImportErrors.BadPort;
    }

    private static string? Transport(Dictionary<string, string> query, out TransportOptions transport)
    {
        var type = (Query(query, "type") ?? "tcp").ToLowerInvariant();
        TransportKind? kind = type switch
        {
            "tcp" or "raw" => TransportKind.Tcp,
            "ws" or "websocket" => TransportKind.WebSocket,
            "grpc" => TransportKind.Grpc,
            "httpupgrade" => TransportKind.HttpUpgrade,
            "xhttp" or "splithttp" => TransportKind.Xhttp,
            _ => null,
        };
        if (kind is not { } known)
        {
            transport = TransportOptions.Tcp;
            return ImportErrors.UnsupportedTransport;
        }

        transport = known == TransportKind.Tcp
            ? TransportOptions.Tcp
            : new TransportOptions(
                known,
                known == TransportKind.Grpc ? null : Query(query, "path"),
                known == TransportKind.Grpc ? null : Query(query, "host"),
                known == TransportKind.Grpc ? Query(query, "serviceName") : null);
        return null;
    }

    private static string? Security(Dictionary<string, string> query, TransportSecurity byDefault, out SecurityOptions security)
    {
        var text = Query(query, "security")?.ToLowerInvariant();
        TransportSecurity? kind = text switch
        {
            null => byDefault,
            "none" => TransportSecurity.None,
            "tls" => TransportSecurity.Tls,
            "reality" => TransportSecurity.Reality,
            _ => null,
        };
        if (kind is not { } known)
        {
            security = SecurityOptions.None;
            return ImportErrors.UnsupportedParameter;
        }

        var alpn = Query(query, "alpn")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tls = known == TransportSecurity.Tls;
        var pinned = tls ? Pinned(Query(query, "pcs")) : [];
        var names = tls ? Query(query, "vcn")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        security = known == TransportSecurity.None
            ? SecurityOptions.None
            : new SecurityOptions(
                known,
                Query(query, "sni") ?? Query(query, "peer"),
                Query(query, "fp"),
                alpn is { Length: > 0 } ? alpn : null,
                IsTrue(Query(query, "allowInsecure") ?? Query(query, "insecure") ?? Query(query, "allow_insecure")),
                known == TransportSecurity.Reality ? Query(query, "pbk") : null,
                known == TransportSecurity.Reality ? Query(query, "sid") : null,
                known == TransportSecurity.Reality ? Query(query, "spx") : null,
                pinned is { Count: > 0 } ? pinned : null,
                names is { Length: > 0 } ? names : null);

        // Закрепленный сертификат с ошибкой: без него сервер проверялся бы иначе, чем задумано (решение 480).
        if (pinned is null)
        {
            return ImportErrors.UnsupportedParameter;
        }

        // REALITY без открытого ключа сервера не подключится.
        return known == TransportSecurity.Reality && security.RealityPublicKey is null ? ImportErrors.NoCredentials : null;
    }

    /// <summary>
    /// «pcs» — SHA-256 сертификатов через запятую, как у Xray: 64 знака hex, двоеточия OpenSSL допустимы. Результат — строчные
    /// без двоеточий; пусто — пустой список; хоть одно значение не SHA-256 — null.
    /// </summary>
    internal static List<string>? Pinned(string? text)
    {
        var hashes = new List<string>();
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hash = part.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            {
                return null;
            }

            hashes.Add(hash);
        }

        return hashes;
    }

    // ── Мелочи ───────────────────────────────────────────────────────────────

    private static LinkResult Ok(
        string name, ProxyProtocol protocol, string host, int port, TransportOptions transport,
        SecurityOptions security, ProtocolOptions options, ServerCredentials credentials)
    {
        var (country, cleanName) = CountryFlag.Split(name);
        return new LinkResult(
            new ServerDefinition(cleanName.Length == 0 ? host : cleanName, country, protocol, host, port, transport, security, options, credentials),
            null);
    }

    private static LinkResult Fail(string error) => new(null, error);

    /// <summary>Первая ошибка из проверок по порядку; проверки уже выполнены все.</summary>
    private static string? FirstError(params string?[] errors) => errors.FirstOrDefault(e => e is not null);

    /// <summary>Значение параметра; пустое — как отсутствующее.</summary>
    private static string? Query(Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static bool IsTrue(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // «+» не превращается в пробел: в base64 ключах он — часть данных.
    private static string Unescape(string text) => Uri.UnescapeDataString(text);

    private static bool IsHost(string host) =>
        host.Length is > 0 and <= 253 && !host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '?' or '#' or '[' or ']');

    private static bool TryPort(string? text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>Строковое поле JSON; числа (порт в vmess бывает числом) — их текст.</summary>
    private static string? Field(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    /// <summary>base64 обычный или url-safe, с выравниванием «=» или без; null — не base64 или не UTF-8.</summary>
    internal static string? DecodeBase64(string text)
    {
        var compact = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                compact.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
        }

        while (compact.Length % 4 != 0)
        {
            compact.Append('=');
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(compact.ToString()));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
