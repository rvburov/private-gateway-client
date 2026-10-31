using System.Net;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>Процесс машины: номер, имя (без .exe) и путь к файлу, если ОС его дает.</summary>
public sealed record RunningProcess(int Id, string Name, string? Path);

/// <summary>Чем выдала себя другая программа VPN или прокси.</summary>
public enum OtherClientSign
{
    /// <summary>Ее туннель держит маршрут ко всему интернету; подробность — имя адаптера.</summary>
    Adapter,

    /// <summary>Работает ее ядро — оно ведет трафик; подробность — имя процесса.</summary>
    Process,

    /// <summary>Системный прокси ведет на ее вход на этой машине; подробность — «адрес:порт».</summary>
    SystemProxy,
}

/// <summary>
/// Другая программа VPN или прокси и чем она себя выдала. <paramref name="Program"/> — название (Hiddify, v2rayN…); не узнана —
/// пусто: окно пишет «другая программа». Одна программа — несколько записей, по одной на признак.
/// </summary>
public sealed record OtherClient(string Program, OtherClientSign Sign, string Detail);

/// <summary>Найденные другие программы VPN и прокси — список со сравнением по содержимому: снимок состояния сравнивается целиком.</summary>
public sealed record OtherClientList(IReadOnlyList<OtherClient> Items)
{
    /// <summary>Названия программ без повторов, по порядку находок; не узнанные — <paramref name="unknown"/>.</summary>
    public IReadOnlyList<string> Programs(string unknown) =>
        [.. Items.Select(i => i.Program.Length > 0 ? i.Program : unknown).Distinct(StringComparer.Ordinal)];

    public bool Equals(OtherClientList? other) => other is not null && Items.SequenceEqual(other.Items);

    public override int GetHashCode() => Items.Aggregate(0, (hash, item) => HashCode.Combine(hash, item));
}

/// <summary>
/// Другие программы VPN и прокси, которые сейчас ведут трафик машины (решение 507). Рядом с ними защита не включается (как на
/// iPhone, где VPN один): два VPN сразу — трафик может пойти мимо туннеля, а внешний IP показал бы чужой сервер. Признаки:
/// туннель другой программы с маршрутом ко всему интернету (<see cref="OtherVpn"/>); работающее ядро другой программы
/// (Xray, sing-box, mihomo…, кроме своих — по пути файла); системный прокси, который ведет на вход другой программы на этой
/// машине и этот вход слушает. Окно программы без работающего ядра (Hiddify открыт, но выключен) — не помеха: трафик она не
/// ведет. Название программы — по процессу, адаптеру или владельцу входа прокси.
/// </summary>
public static class OtherClients
{
    // Окна программ: по ним — только название, сами они трафик не ведут (кроме тех, чье ядро внутри, — их выдает вход прокси).
    private static readonly Dictionary<string, string> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hiddify"] = "Hiddify", ["HiddifyNext"] = "Hiddify", ["hiddify-next"] = "Hiddify", ["Hiddify Next"] = "Hiddify",
        ["v2rayN"] = "v2rayN", ["v2rayA"] = "v2rayA", ["V2rayU"] = "V2rayU", ["V2Box"] = "V2Box",
        ["nekoray"] = "NekoRay", ["nekobox"] = "NekoBox", ["Throne"] = "Throne",
        ["Clash for Windows"] = "Clash", ["clash-verge"] = "Clash Verge", ["Clash Verge"] = "Clash Verge", ["clash-nyanpasu"] = "Clash Nyanpasu",
        ["ClashX"] = "ClashX", ["ClashX Pro"] = "ClashX", ["ClashX Meta"] = "ClashX", ["FlClash"] = "FlClash", ["Mihomo Party"] = "Mihomo Party",
        ["Outline"] = "Outline", ["Shadowsocks"] = "Shadowsocks", ["ShadowsocksX-NG"] = "Shadowsocks", ["Shadowrocket"] = "Shadowrocket",
        ["Happ"] = "Happ", ["Karing"] = "Karing", ["FoXray"] = "FoXray", ["Streisand"] = "Streisand", ["Invisible Man - Xray"] = "Invisible Man",
        ["sing-box"] = "sing-box", ["SFM"] = "sing-box", ["Amnezia"] = "AmneziaVPN", ["AmneziaVPN"] = "AmneziaVPN",
    };

    // Ядра: работают, только пока программа ведет трафик (или все время, пока открыта, — тогда она и ведет).
    private static readonly Dictionary<string, string> Cores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xray"] = "Xray", ["v2ray"] = "V2Ray", ["sing-box"] = "sing-box", ["mihomo"] = "mihomo", ["clash"] = "Clash", ["clash-meta"] = "Clash",
        ["verge-mihomo"] = "Clash Verge", ["hysteria"] = "Hysteria", ["tuic-client"] = "TUIC", ["naive"] = "NaiveProxy", ["tun2socks"] = "tun2socks",
        ["HiddifyCli"] = "Hiddify", ["hiddify-core"] = "Hiddify", ["nekobox_core"] = "NekoBox", ["nekoray_core"] = "NekoRay",
        ["clash-win64"] = "Clash", ["sslocal"] = "Shadowsocks", ["ss-local"] = "Shadowsocks", ["trojan"] = "Trojan", ["trojan-go"] = "Trojan", ["brook"] = "Brook",
        ["juicity-client"] = "Juicity", ["outline-go-tun2socks"] = "Outline", ["amneziawg"] = "AmneziaVPN",
    };

    // Имена адаптеров туннелей других программ — для названия; не узнан — по окну программы, если оно одно.
    private static readonly (string Part, string Program)[] Adapters =
    [
        ("hiddify", "Hiddify"), ("outline", "Outline"), ("nekoray", "NekoRay"), ("nekobox", "NekoBox"), ("throne", "Throne"),
        ("clash", "Clash"), ("mihomo", "Clash"), ("meta", "Clash"), ("v2rayn", "v2rayN"), ("amnezia", "AmneziaVPN"),
        ("wireguard", "WireGuard"), ("openvpn", "OpenVPN"), ("tailscale", "Tailscale"), ("zerotier", "ZeroTier"), ("proton", "Proton VPN"),
        ("nord", "NordVPN"), ("mullvad", "Mullvad"), ("windscribe", "Windscribe"), ("warp", "Cloudflare WARP"), ("cloudflare", "Cloudflare WARP"),
        ("expressvpn", "ExpressVPN"), ("surfshark", "Surfshark"), ("kaspersky", "Kaspersky VPN"), ("adguard", "AdGuard VPN"),
    ];

    /// <summary>Имя процесса — окно или ядро известной программы VPN или прокси: только у таких нужен путь к файлу.</summary>
    public static bool IsKnown(string name) => Apps.ContainsKey(name) || Cores.ContainsKey(name);

    /// <summary>
    /// Найти другие программы. <paramref name="processes"/> — процессы машины; <paramref name="own"/> — пути своих ядер (их
    /// процессы не в счет); <paramref name="tunnels"/> — адаптеры чужих туннелей с маршрутом ко всему интернету
    /// (<see cref="OtherVpn.Find"/>); <paramref name="proxy"/> — системный прокси другой программы, если его вход на этой машине
    /// слушает, и номер процесса-владельца входа (null — неизвестен).
    /// </summary>
    public static IReadOnlyList<OtherClient> Find(IReadOnlyList<RunningProcess> processes, IReadOnlyCollection<string> own,
        IReadOnlyList<string> tunnels, (string Address, int? Owner)? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(tunnels);
        var apps = processes.Where(p => Apps.ContainsKey(p.Name) && !IsOwn(p, own)).ToList();
        var found = new List<OtherClient>();
        foreach (var core in processes.Where(p => Cores.ContainsKey(p.Name) && !Apps.ContainsKey(p.Name) && !IsOwn(p, own)))
        {
            found.Add(new OtherClient(Owner(core, apps), OtherClientSign.Process, core.Name));
        }

        // Окно sing-box (SFM на macOS, sing-box для Windows как служба) — оно и есть ядро.
        foreach (var app in apps.Where(a => Cores.ContainsKey(a.Name)))
        {
            found.Add(new OtherClient(Apps[app.Name], OtherClientSign.Process, app.Name));
        }

        foreach (var adapter in tunnels)
        {
            var named = Adapters.FirstOrDefault(a => adapter.Contains(a.Part, StringComparison.OrdinalIgnoreCase)).Program;
            found.Add(new OtherClient(named ?? Single(apps) ?? string.Empty, OtherClientSign.Adapter, adapter));
        }

        if (proxy is var (address, owner))
        {
            var process = owner is { } id ? processes.FirstOrDefault(p => p.Id == id) : null;
            var program = process is null ? Single(apps) ?? string.Empty
                : Apps.TryGetValue(process.Name, out var app) ? app
                : Cores.ContainsKey(process.Name) ? Owner(process, apps)
                : process.Name;
            found.Add(new OtherClient(program, OtherClientSign.SystemProxy, address));
        }

        return found;
    }

    /// <summary>
    /// Системный прокси указывает на вход на этой машине (127.0.0.1, ::1, localhost) — «адрес:порт» в порт; иначе (сервер в
    /// сети, скрипт автонастройки) — null: это не программа на этой машине.
    /// </summary>
    public static int? LocalPort(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address.AsSpan(colon + 1), out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        var host = address[..colon].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
            ? port
            : null;
    }

    private static bool IsOwn(RunningProcess process, IReadOnlyCollection<string> own) =>
        process.Path is { } path && own.Any(o => string.Equals(o, path, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));

    /// <summary>Чье ядро: окно программы из той же папки (ядро v2rayN — в v2rayN\bin\xray), иначе окно, если оно одно, иначе само ядро.</summary>
    private static string Owner(RunningProcess core, List<RunningProcess> apps)
    {
        var folder = apps.FirstOrDefault(a => a.Path is { } app && core.Path is { } path && Folder(app) is { Length: > 3 } dir
            && path.Length > dir.Length && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && path[dir.Length] is '/' or '\\');
        return folder is not null ? Apps[folder.Name] : Single(apps) ?? Cores[core.Name];
    }

    /// <summary>Папка файла — с путями и Windows, и Unix: служба одна, а пути приходят от ОС, где она работает.</summary>
    private static string Folder(string path) => path[..Math.Max(0, Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')))];

    private static string? Single(List<RunningProcess> apps) =>
        apps.Select(a => Apps[a.Name]).Distinct(StringComparer.Ordinal).ToList() is [var only] ? only : null;
}
