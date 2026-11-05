using System.Net;
using System.Text;

namespace PrivateGateway.Client.Core.Servers;

/// <summary>Чистая проверка внешнего HTTP/SOCKS5-прокси: без поиска имен и сетевых запросов.</summary>
public static class ForwardProxyRules
{
    public static bool IsForwardProxy(ProxyProtocol protocol) => protocol is ProxyProtocol.Http or ProxyProtocol.Socks5;

    /// <summary>HTTP CONNECT допускает TLS до прокси; обычный SOCKS5 используется без дополнительной TLS-обертки.</summary>
    public static bool AcceptsTransport(ProxyProtocol protocol, TransportKind transport, TransportSecurity security) =>
        transport == TransportKind.Tcp && (protocol == ProxyProtocol.Http && security is TransportSecurity.None or TransportSecurity.Tls
            || protocol == ProxyProtocol.Socks5 && security == TransportSecurity.None);

    /// <summary>
    /// Внешний адрес указывает в собственный локальный вход: только буквальная петля или localhost и тот же порт.
    /// Одинаковый порт у удаленного сервера допустим; имена для этой проверки не разрешаются.
    /// </summary>
    public static bool IsLocalLoop(string? host, int externalPort, int localPort)
    {
        if (externalPort != localPort || externalPort is < 1 or > 65535 || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim().TrimEnd('.');
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var address)
            && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    /// <summary>Только адрес узла без схемы, учетных данных, пути и порта; IPv6 — без квадратных скобок.</summary>
    public static bool IsValidEndpoint(string? host, int port)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host) || host.Length > 253
            || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) || host.IndexOfAny(['/', '\\', '@', '?', '#', '[', ']']) >= 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
        }

        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    /// <summary>
    /// Без авторизации — оба поля пусты; иначе нужна полная пара. SOCKS5 ограничивает длину каждого поля 255 байтами,
    /// HTTP Basic не допускает двоеточия в имени. Выбранный пользователем режим UserPassword проверяется отдельно:
    /// отсутствие сохраненного секрета не разрешает переход к анонимному доступу.
    /// </summary>
    public static bool AcceptsCredentials(ProxyProtocol protocol, ServerCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!IsForwardProxy(protocol))
        {
            return false;
        }

        var user = credentials.Username;
        var password = credentials.Password;
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password))
        {
            return true;
        }

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || user.Length > 4096 || password.Length > 4096
            || user.Any(char.IsControl) || password.Any(char.IsControl))
        {
            return false;
        }

        return protocol == ProxyProtocol.Socks5
            ? Encoding.UTF8.GetByteCount(user) <= 255 && Encoding.UTF8.GetByteCount(password) <= 255
            : !user.Contains(':', StringComparison.Ordinal);
    }
}
