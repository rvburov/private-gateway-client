using System.Net;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>Процесс машины: номер, имя (без .exe) и путь к файлу, если ОС его дает.</summary>
public sealed record RunningProcess(int Id, string Name, string? Path);

/// <summary>Чем выдал себя другой VPN или прокси-клиент.</summary>
public enum OtherClientSign
{
    /// <summary>Его туннель держит маршрут ко всему интернету; подробность — имя адаптера (на экран не идет).</summary>
    Adapter,

    /// <summary>Работает ядро прокси (Xray, sing-box, mihomo…) — оно ведет трафик; подробность — имя процесса (на экран не идет).</summary>
    Process,

    /// <summary>Системный прокси ведет на вход на этой машине, и его слушает не свое ядро; подробность — «адрес:порт».</summary>
    SystemProxy,
}

/// <summary>
/// Признак другого VPN или прокси-клиента. Какая это программа, не определяется и не показывается (решение 525, просьба
/// пользователя 2026-11-01): защита отвечает на то, что видно на машине, — ядро, туннель, системный прокси.
/// </summary>
public sealed record OtherClient(OtherClientSign Sign, string Detail);

/// <summary>Найденные признаки — список со сравнением по содержимому: снимок состояния сравнивается целиком.</summary>
public sealed record OtherClientList(IReadOnlyList<OtherClient> Items)
{
    public bool Equals(OtherClientList? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Aggregate(0, (hash, item) => HashCode.Combine(hash, item));
}

/// <summary>
/// Другие VPN и прокси-клиенты, которые сейчас ведут трафик машины (решения 507, 511, 525). Рядом с ними защита не
/// включается (как на iPhone, где VPN один): два клиента сразу — трафик может пойти мимо туннеля, а внешний IP показал бы
/// чужой сервер. Признаки общие для любого клиента: туннель с маршрутом ко всему интернету (<see cref="OtherVpn"/>);
/// работающее ядро прокси, кроме своих (по пути файла); системный прокси, который ведет на вход на этой машине, и его
/// слушает не свое ядро. Окно клиента без работающего ядра трафик не ведет — не помеха. Названий программ здесь нет:
/// ядро узнается по имени процесса движка, а не программы, в которую оно встроено.
/// </summary>
public static class OtherClients
{
    // Процессы ядер прокси: сами движки (Xray, sing-box, mihomo…) и их сборки внутри клиентов. Только для поиска — на экран
    // имена не идут.
    private static readonly HashSet<string> Cores = new(StringComparer.OrdinalIgnoreCase)
    {
        "xray", "v2ray", "sing-box", "mihomo", "clash", "clash-meta", "verge-mihomo", "clash-win64", "hysteria", "tuic-client", "naive",
        "tun2socks", "HiddifyCli", "hiddify-core", "nekobox_core", "nekoray_core", "sslocal", "ss-local", "trojan", "trojan-go", "brook",
        "juicity-client", "outline-go-tun2socks", "amneziawg",
    };

    /// <summary>Имя процесса — ядро прокси: только у таких нужен путь к файлу (свое ли).</summary>
    public static bool IsKnown(string name) => Cores.Contains(name);

    /// <summary>
    /// Найти признаки. <paramref name="processes"/> — процессы машины; <paramref name="own"/> — пути своих ядер (их процессы
    /// не в счет); <paramref name="tunnels"/> — адаптеры чужих туннелей с маршрутом ко всему интернету
    /// (<see cref="OtherVpn.Find"/>); <paramref name="proxy"/> — системный прокси другой программы, если его вход на этой
    /// машине слушает, и номер процесса-владельца входа (null — неизвестен).
    /// </summary>
    public static IReadOnlyList<OtherClient> Find(IReadOnlyList<RunningProcess> processes, IReadOnlyCollection<string> own,
        IReadOnlyList<string> tunnels, (string Address, int? Owner)? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(tunnels);
        var found = new List<OtherClient>();
        found.AddRange(processes.Where(p => Cores.Contains(p.Name) && !IsOwn(p, own)).Select(p => new OtherClient(OtherClientSign.Process, p.Name)));
        found.AddRange(tunnels.Select(adapter => new OtherClient(OtherClientSign.Adapter, adapter)));

        // Вход слушает свое ядро (режим прокси) — это не другая программа.
        if (proxy is var (address, owner) && !(owner is { } id && processes.FirstOrDefault(p => p.Id == id) is { } listener && IsOwn(listener, own)))
        {
            found.Add(new OtherClient(OtherClientSign.SystemProxy, address));
        }

        return found;
    }

    /// <summary>
    /// Системный прокси указывает на вход на этой машине (127.0.0.1, ::1, localhost) — порт; иначе (сервер в сети, скрипт
    /// автонастройки, не разобрать) — null: это не программа на этой машине. Адрес — как его пишут ОС и программы:
    /// «узел:порт», со схемой и именем («схема://имя@адрес:порт/»), списком Windows («http=узел:порт;https=…» — первый).
    /// Со схемой (http, socks5) его ставят и клиенты на Windows, и раньше такой вход не узнавался.
    /// </summary>
    public static int? LocalPort(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.Trim();
        if (text.IndexOf(';', StringComparison.Ordinal) is var list and >= 0)
        {
            text = text[..list];
        }

        if (text.IndexOf("://", StringComparison.Ordinal) is var scheme and >= 0)
        {
            text = text[(scheme + 3)..];
        }
        else if (text.IndexOf('=', StringComparison.Ordinal) is var protocol and >= 0)
        {
            text = text[(protocol + 1)..];
        }

        text = text[(text.LastIndexOf('@') + 1)..];
        if (text.IndexOfAny(['/', '?', '#']) is var end and >= 0)
        {
            text = text[..end];
        }

        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text.AsSpan(colon + 1), out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        var host = text[..colon].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            ? port
            : null;
    }

    private static bool IsOwn(RunningProcess process, IReadOnlyCollection<string> own) =>
        process.Path is { } path && own.Any(o => string.Equals(o, path, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
}
