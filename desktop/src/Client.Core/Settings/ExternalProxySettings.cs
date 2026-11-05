using System.Globalization;
using System.Net;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Core.Settings;

/// <summary>Протокол внешнего прокси; локальный HTTP/SOCKS-вход настраивается отдельно.</summary>
public enum ExternalProxyProtocol
{
    Socks5,
    Http,
    Https,
}

/// <summary>Отсутствие логина включает и прокси с разрешением по внешнему IP на стороне владельца.</summary>
public enum ProxyAuthentication
{
    None,
    UserPassword,
}

/// <summary>
/// Несекретные параметры внешнего выхода режима «Только прокси». Логин и пароль находятся в хранилище ОС;
/// <paramref name="CredentialsKey"/> — только ссылка на отдельную запись. Порт не связан с локальным входом ядра.
/// </summary>
public sealed record ExternalProxySettings(
    ExternalProxyProtocol Protocol = ExternalProxyProtocol.Socks5,
    string Host = "",
    int Port = 1080,
    ProxyAuthentication Authentication = ProxyAuthentication.None,
    string? CredentialsKey = null,
    long Revision = 0)
{
    public const string ServerId = "external-proxy";

    public const string Capability = "external-proxy-v1";

    public const string CredentialsPrefix = "external-proxy:";

    public static ExternalProxySettings Default { get; } = new();

    /// <summary>Адрес и протокол корректны; готовность обязательных учетных данных проверяется отдельно хранилищем.</summary>
    public bool IsConfigured => Normalize() is not null;

    /// <summary>Нормализованный IP/IDN либо null. Здесь нет DNS-запроса и приема URL с учетными данными.</summary>
    public ExternalProxySettings? Normalize()
    {
        if (!Enum.IsDefined(Protocol) || !Enum.IsDefined(Authentication) || Port is < 1 or > 65535 || Revision < 0
            || Host is null || Host.Any(char.IsControl))
        {
            return null;
        }

        var host = Host.Trim();
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || host.IndexOfAny(['/', '\\', '@', '?', '#', '%']) >= 0)
        {
            return null;
        }

        if (host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }

        if (IPAddress.TryParse(host, out var address))
        {
            host = address.ToString();
        }
        else
        {
            try
            {
                host = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return null;
            }

            var name = host.TrimEnd('.');
            if (name.Length is 0 or > 253 || host.Length > 254 || host.EndsWith("..", StringComparison.Ordinal)
                || name.Split('.').Any(label => label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-'
                    || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            {
                return null;
            }
        }

        return ForwardProxyRules.IsValidEndpoint(host, Port)
            ? this with { Host = host, CredentialsKey = IsCredentialsKey(CredentialsKey) ? CredentialsKey : null }
            : null;
    }

    /// <summary>Ключи внешнего прокси не дают прочитать посторонние записи того же хранилища.</summary>
    public static bool IsCredentialsKey(string? key) => key is not null
        && key.StartsWith(CredentialsPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(key[CredentialsPrefix.Length..], "N", out _);

    /// <summary>Поколение цели не смешивается с VPN id и предыдущим активным внешним подключением.</summary>
    public static bool IsExternalId(string? id) => id == ServerId || id is not null
        && id.StartsWith(ServerId + ":", StringComparison.Ordinal)
        && long.TryParse(id[(ServerId.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision >= 0;

    /// <summary>Метаданные для существующего сценария подключения, без логина и пароля.</summary>
    public ServerProfile? ToProfile() => Normalize() is { } normalized
        ? new ServerProfile(ServerId + ":" + normalized.Revision.ToString(CultureInfo.InvariantCulture), "External proxy", string.Empty,
            normalized.Protocol == ExternalProxyProtocol.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http,
            normalized.Protocol == ExternalProxyProtocol.Https ? TransportSecurity.Tls : TransportSecurity.None,
            normalized.Host, normalized.Port, "tcp", null)
        : null;
}
