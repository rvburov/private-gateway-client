using System.Net;
using System.Text.Json;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Import;

/// <summary>Первый участок маршрута из пакета подключения. Учетные данные передаются только хранилищу секретов ОС.</summary>
public sealed record EntryChainProxy(string Host, int Port, string Username, string Password, bool SupportsUdp)
{
    public override string ToString() => $"EntryChainProxy {{ Host = {Host}, Port = {Port}, SupportsUdp = {SupportsUdp} }}";
}

/// <summary>Профиль пакета и его постоянный идентификатор на сайте.</summary>
public sealed record EntryChainProfile(string Id, ServerDefinition Server);

/// <summary>Версионированный пакет: один входной SOCKS5 и варианты VLESS одного узла.</summary>
public sealed record EntryChainBundle(string DeviceId, string NodeId, Uri SubscriptionUrl, EntryChainProxy EntryProxy,
    IReadOnlyList<EntryChainProfile> Profiles)
{
    public override string ToString() => $"EntryChainBundle {{ DeviceId = {DeviceId}, NodeId = {NodeId}, Profiles = {Profiles.Count} }}";
}

/// <summary>Пакет либо полностью пригоден к импорту, либо целиком отклонен.</summary>
public sealed record EntryChainParseResult(EntryChainBundle? Bundle, ImportBatch Batch);

/// <summary>Разбор собственного JSON v1 без сетевых запросов и без вывода ключей в ошибки.</summary>
public static class EntryChainImport
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 16,
    };

    public static EntryChainParseResult? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('{'))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entry_proxy", out _))
            {
                return null;
            }

            return Parse(root);
        }
        catch (JsonException)
        {
            // Если пакет поврежден, sing-box не должен принять его как обычный конфиг.
            return text.Contains("\"entry_proxy\"", StringComparison.Ordinal)
                ? Broken(ImportErrors.EntryBundleInvalid) : null;
        }
        catch (EntryChainException error)
        {
            return Broken(error.Code);
        }
    }

    private static EntryChainParseResult Parse(JsonElement root)
    {
        RequireKeys(root, "version", "device_id", "node_id", "subscription_url", "entry_proxy", "profiles");
        if (Integer(root, "version") != 1)
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        var deviceId = Identifier(root, "device_id");
        var nodeId = Identifier(root, "node_id");
        var subscriptionUrl = SubscriptionUrl(root);
        var proxy = Proxy(Object(root, "entry_proxy"));
        var profilesElement = Property(root, "profiles");
        if (profilesElement.ValueKind != JsonValueKind.Array || profilesElement.GetArrayLength() is < 1 or > 32)
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        var profiles = new List<EntryChainProfile>();
        var lines = new List<ImportLine>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var line = 0;
        foreach (var element in profilesElement.EnumerateArray())
        {
            line++;
            EntryChainProfile profile;
            try
            {
                profile = Profile(element);
            }
            catch (EntryChainException error) when (error.Code == ImportErrors.EntryH3Unsupported)
            {
                // Показать неподдерживаемый H3 в предпросмотре, сохранив пригодные TCP-профили.
                lines.Add(new ImportLine(line, new LinkResult(null, error.Code)));
                continue;
            }

            if (!ids.Add(profile.Id))
            {
                throw new EntryChainException(ImportErrors.EntryBundleInvalid);
            }

            if (profile.Id != deviceId + ":" + (profile.Server.Transport.Kind == TransportKind.Tcp ? "tcp" : "xhttp"))
            {
                throw new EntryChainException(ImportErrors.EntryBundleInvalid);
            }

            profiles.Add(profile);
            lines.Add(new ImportLine(line, new LinkResult(profile.Server, null)));
        }

        return new EntryChainParseResult(profiles.Count == 0 ? null : new EntryChainBundle(deviceId, nodeId, subscriptionUrl, proxy, profiles),
            new ImportBatch(ImportParser.FormatEntryChain, lines));
    }

    private static Uri SubscriptionUrl(JsonElement root)
    {
        var value = String(root, "subscription_url");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length != 0 || url.Fragment.Length != 0 || url.Query.Length != 0
            || !url.AbsolutePath.StartsWith("/sub/entry/", StringComparison.Ordinal)
            || url.AbsolutePath.Length <= "/sub/entry/".Length)
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        return url;
    }

    private static EntryChainProxy Proxy(JsonElement element)
    {
        RequireKeysWithOptional(element, ["type", "host", "port", "supports_udp"], "username", "password");
        if (String(element, "type") != "socks5")
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        var supportsUdp = Boolean(element, "supports_udp");

        var settings = new ExternalProxySettings(Host: String(element, "host"), Port: Integer(element, "port")).Normalize();
        if (settings is null)
        {
            throw new EntryChainException(ImportErrors.BadHost);
        }

        var hasUsername = element.TryGetProperty("username", out _);
        var hasPassword = element.TryGetProperty("password", out _);
        if (hasUsername != hasPassword)
        {
            throw new EntryChainException(ImportErrors.NoCredentials);
        }

        var username = OptionalString(element, "username");
        var password = OptionalString(element, "password");
        if (!ForwardProxyRules.AcceptsCredentials(ProxyProtocol.Socks5, new ServerCredentials(Username: username, Password: password)))
        {
            throw new EntryChainException(ImportErrors.NoCredentials);
        }

        return new EntryChainProxy(settings.Host, settings.Port, username, password, supportsUdp);
    }

    private static EntryChainProfile Profile(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        var security = String(element, "security");
        if (security != "reality")
        {
            // TLS/H3 требует UDP relay и отдельного правила защиты, которых в v1 пока нет.
            var h3 = security == "tls" && element.TryGetProperty("alpn", out var alpn)
                && alpn.ValueKind == JsonValueKind.Array && alpn.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "h3");
            throw new EntryChainException(h3 ? ImportErrors.EntryH3Unsupported : ImportErrors.EntryBundleInvalid);
        }

        var transport = String(element, "transport");
        if (transport == "tcp")
        {
            RequireKeysWithOptional(element, ["id", "name", "transport", "host", "port", "uuid", "security", "sni", "public_key",
                "fingerprint", "flow", "spider_x"], "short_id");
        }
        else if (transport == "xhttp")
        {
            RequireKeysWithOptional(element, ["id", "name", "transport", "host", "port", "uuid", "security", "sni", "public_key",
                "fingerprint", "path", "mode"], "short_id");
        }
        else
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        var id = Identifier(element, "id");
        var name = String(element, "name").Trim();
        var host = String(element, "host");
        var port = Integer(element, "port");
        var uuid = String(element, "uuid");
        var sni = String(element, "sni");
        var publicKey = String(element, "public_key");
        var shortId = OptionalString(element, "short_id");
        var fingerprint = String(element, "fingerprint");
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)
            || !ForwardProxyRules.IsValidEndpoint(host, port) || !IPAddress.TryParse(host, out _)
            || !ForwardProxyRules.IsValidEndpoint(sni, 443)
            || !Guid.TryParseExact(uuid, "D", out _) || !ValidPublicKey(publicKey)
            || shortId.Length > 16 || shortId.Length % 2 != 0 || !shortId.All(Uri.IsHexDigit)
            || fingerprint != "chrome")
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        TransportOptions options;
        string? flow = null;
        string? spiderX = null;
        if (transport == "tcp")
        {
            flow = String(element, "flow");
            spiderX = String(element, "spider_x");
            if (flow != "xtls-rprx-vision" || spiderX != "/")
            {
                throw new EntryChainException(ImportErrors.EntryBundleInvalid);
            }

            options = TransportOptions.Tcp;
        }
        else
        {
            var path = String(element, "path");
            if (String(element, "mode") != "auto" || path.Length is < 1 or > 1024 || !path.StartsWith('/')
                || path.Any(char.IsControl))
            {
                throw new EntryChainException(ImportErrors.EntryBundleInvalid);
            }

            options = new TransportOptions(TransportKind.Xhttp, Path: path, Mode: "auto");
        }

        var definition = new ServerDefinition(name, string.Empty, ProxyProtocol.Vless, host, port, options,
            new SecurityOptions(TransportSecurity.Reality, Sni: sni, Fingerprint: fingerprint,
                RealityPublicKey: publicKey, RealityShortId: shortId, RealitySpiderX: spiderX),
            new ProtocolOptions(Flow: flow), new ServerCredentials(Uuid: uuid));
        return new EntryChainProfile(id, definition);
    }

    private static bool ValidPublicKey(string value)
    {
        if (value.Length != 43 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=").Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static EntryChainParseResult Broken(string code) => new(null,
        new ImportBatch(ImportParser.FormatEntryChain, [new ImportLine(1, new LinkResult(null, code))]));

    private static JsonElement Object(JsonElement element, string name)
    {
        var value = Property(element, name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }

        return value;
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value : throw new EntryChainException(ImportErrors.EntryBundleInvalid);

    private static string String(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text : throw new EntryChainException(ImportErrors.EntryBundleInvalid);
    }

    private static string OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out _) ? String(element, name) : string.Empty;

    private static int Integer(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : throw new EntryChainException(ImportErrors.EntryBundleInvalid);
    }

    private static bool Boolean(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new EntryChainException(ImportErrors.EntryBundleInvalid),
        };
    }

    private static string Identifier(JsonElement element, string name)
    {
        var value = String(element, name);
        return value.Length is > 0 and <= 128 && !value.Any(char.IsControl)
            ? value : throw new EntryChainException(ImportErrors.EntryBundleInvalid);
    }

    private static void RequireKeys(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Any(p => !keys.Contains(p.Name, StringComparer.Ordinal))
            || keys.Any(key => !element.TryGetProperty(key, out _)))
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }
    }

    private static void RequireKeysWithOptional(JsonElement element, string[] required, params string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.EnumerateObject().Any(p => !required.Contains(p.Name, StringComparer.Ordinal)
                && !optional.Contains(p.Name, StringComparer.Ordinal))
            || required.Any(key => !element.TryGetProperty(key, out _)))
        {
            throw new EntryChainException(ImportErrors.EntryBundleInvalid);
        }
    }

    private sealed class EntryChainException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
