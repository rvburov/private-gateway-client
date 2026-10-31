using System.Globalization;
using System.Net;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>Что за настройка компьютера может мешать подключению.</summary>
public enum HindranceKind
{
    /// <summary>Переменная окружения прокси: http_proxy, https_proxy, all_proxy, no_proxy — ее берут curl, git, pip, .NET и другие.</summary>
    EnvironmentProxy,

    /// <summary>Системный прокси другой программы (параметры Интернета, gsettings, kioslaverc, scutil).</summary>
    SystemProxy,

    /// <summary>Скрипт автонастройки прокси (PAC).</summary>
    AutoConfig,

    /// <summary>Автоопределение прокси (WPAD, Windows): обычно не мешает, но в сети с WPAD трафик уходит на ее прокси.</summary>
    AutoDetect,

    /// <summary>Прокси WinHTTP (Windows): его берут службы Windows и программы на WinHTTP — обновления, магазин, часть установщиков.</summary>
    WinHttpProxy,
}

/// <summary>Чья настройка: пользователя, всей машины или сеанса, в котором работает окно (Linux, macOS — из окружения).</summary>
public enum HindranceScope
{
    User,
    Machine,
    Session,
}

/// <summary>
/// Настройка компьютера, которая может мешать подключению: что (<paramref name="Kind"/>), имя (переменная, «ProxyServer»…),
/// значение как есть (<paramref name="Value"/> — на экран и в отчет только <see cref="Shown"/>: имя и пароль прокси скрыты) и чья.
/// </summary>
public sealed record Hindrance(HindranceKind Kind, string Name, string Value, HindranceScope Scope)
{
    /// <summary>Значение для экрана и отчета: имя и пароль в адресе прокси — «***».</summary>
    public string Shown => ProxyAddress.Mask(Value);

    /// <summary>«узел:порт», который проверяет служба: слушает ли его кто-то; не адрес прокси (no_proxy, WPAD) — null.</summary>
    public string? Endpoint => Kind is HindranceKind.AutoDetect || Name.Equals("no_proxy", StringComparison.OrdinalIgnoreCase) ? null : ProxyAddress.Endpoint(Value);

    /// <summary>В значении есть имя или пароль.</summary>
    public bool HasCredentials => ProxyAddress.HasCredentials(Value);
}

/// <summary>Вход прокси на этой машине или в сети: слушает ли его кто-то и кто (имя процесса, если служба его знает).</summary>
public sealed record EntryCheck(string Address, bool Listens, string? Process);

/// <summary>Что ответила служба: входы прокси и другие программы VPN и прокси, которые сейчас работают.</summary>
public sealed record InterferenceProbe(IReadOnlyList<EntryCheck> Entries, IReadOnlyList<OtherClient> Clients);

/// <summary>
/// Настройки компьютера, которые могут мешать (решение 508): переменные окружения прокси, системный прокси другой программы,
/// PAC и WPAD, прокси WinHTTP. Читает окно — это настройки пользователя и машины, а не службы; исправляет только безопасное
/// и только с копией прежнего значения.
/// </summary>
public interface IMachineSettings
{
    /// <summary>Что найдено; свой системный прокси (вход своего ядра в режиме прокси) не в счет.</summary>
    IReadOnlyList<Hindrance> Read(Core.Settings.AppSettings settings);

    /// <summary>
    /// Можно ли исправить из окна: без прав администратора, без имени и пароля в значении и только неработающий прокси —
    /// работающий ставит программа, которая сейчас ведет трафик: ее надо закрыть, а не спорить с ней.
    /// </summary>
    bool CanFix(Hindrance hindrance, EntryCheck? entry);

    /// <summary>Исправить: убрать настройку. Прежнее значение — в файл копии; возвращает его путь.</summary>
    string Fix(Hindrance hindrance);
}

/// <summary>Адрес прокси из настройки: «схема://имя:пароль@узел:порт/путь», «узел:порт» или «узел».</summary>
public static class ProxyAddress
{
    /// <summary>Имя и пароль — «***», остальное как есть.</summary>
    public static string Mask(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var start = value.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0 ? scheme + 3 : 0;
        var at = value.IndexOf('@', start);
        var slash = value.IndexOf('/', start);
        return at > start && (slash < 0 || at < slash) ? value[..start] + "***" + value[at..] : value;
    }

    /// <summary>Есть ли имя или пароль.</summary>
    public static bool HasCredentials(string value) => Mask(value) != value;

    /// <summary>
    /// «узел:порт» прокси; порт не указан — по схеме (http — 80, https — 443, socks — 1080); не разобрать — null. IPv6 — в
    /// квадратных скобках.
    /// </summary>
    public static string? Endpoint(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd > 0 ? text[..schemeEnd].ToLowerInvariant() : "http";
        var rest = schemeEnd > 0 ? text[(schemeEnd + 3)..] : text;
        rest = rest[(rest.IndexOf('@', StringComparison.Ordinal) + 1)..];
        if (rest.IndexOfAny(['/', '?', '#']) is var end and >= 0)
        {
            rest = rest[..end];
        }

        if (rest.Length == 0)
        {
            return null;
        }

        string host;
        string? port = null;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            host = rest[..(close + 1)];
            port = rest.Length > close + 2 && rest[close + 1] == ':' ? rest[(close + 2)..] : null;
        }
        else if (rest.Count(c => c == ':') == 1)
        {
            host = rest[..rest.IndexOf(':', StringComparison.Ordinal)];
            port = rest[(rest.IndexOf(':', StringComparison.Ordinal) + 1)..];
        }
        else if (!rest.Contains(':', StringComparison.Ordinal))
        {
            host = rest;
        }
        else
        {
            return null;
        }

        var number = port is null
            ? scheme switch { "https" => 443, "socks" or "socks4" or "socks4a" or "socks5" or "socks5h" => 1080, _ => 80 }
            : int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 65535 ? parsed : 0;
        return host.Length == 0 || number == 0 || Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown
            ? null
            : host + ":" + number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Прокси на этой машине (127.0.0.1, ::1, localhost).</summary>
    public static bool IsLocal(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var host = endpoint[..endpoint.LastIndexOf(':')].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }
}
