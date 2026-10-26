using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>Семейство адресов фильтра WFP: свои слои у IPv4 и IPv6.</summary>
public enum WfpFamily
{
    V4,
    V6,
}

/// <summary>
/// Где стоит фильтр: установка соединения программ машины (ALE connect и accept) или пересылка чужих пакетов —
/// хот-спот, общий доступ к интернету, виртуальные машины и контейнеры с NAT (IPFORWARD).
/// </summary>
public enum WfpHook
{
    Connection,
    Forward,
}

/// <summary>Поле условия фильтра WFP.</summary>
public enum WfpField
{
    /// <summary>Флаги соединения или пакета; значение — <see cref="WfpRules.LoopbackFlag"/> и другие флаги.</summary>
    Flags,

    /// <summary>Адаптер, через который идет соединение (LUID).</summary>
    LocalInterface,

    RemoteAddress,
    RemotePort,
    LocalPort,

    /// <summary>Протокол: 6 — TCP, 17 — UDP, 58 — ICMPv6.</summary>
    Protocol,

    /// <summary>Тип ICMP — у WFP это то же поле, что локальный порт.</summary>
    IcmpType,

    /// <summary>Программа (ALE_APP_ID); значение — полный путь в <see cref="WfpCondition.Program"/>.</summary>
    App,

    /// <summary>Адрес назначения пересылаемого пакета (IP_DESTINATION_ADDRESS): портов и программ у пересылки нет.</summary>
    DestinationAddress,
}

/// <summary>
/// Условие фильтра. Значение — число, адрес (<see cref="IPAddress"/>), сеть (адрес и длина префикса)
/// или программа (путь). Условия на одно поле объединяются через «или», на разные — через «и».
/// </summary>
public sealed record WfpCondition(WfpField Field, ulong Number = 0, IPAddress? Address = null, int? PrefixLength = null, string? Program = null)
{
    public override string ToString() => Field + " " + (Program ?? (Address is null ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : PrefixLength is { } prefix ? $"{Address}/{prefix}" : Address.ToString()));
}

/// <summary>Фильтр WFP: разрешить или закрыть; чем больше вес, тем раньше он решает.</summary>
public sealed record WfpRule(string Name, WfpFamily Family, bool Permit, byte Weight, IReadOnlyList<WfpCondition> Conditions,
    WfpHook Hook = WfpHook.Connection)
{
    public override string ToString() =>
        $"{Weight} {(Permit ? "permit" : "block")} {Family}{(Hook == WfpHook.Forward ? " forward" : string.Empty)} {Name}: {string.Join(", ", Conditions)}";
}

/// <summary>
/// Политика блока при обрыве (<see cref="KillSwitchPolicy"/>) в фильтрах WFP — чистая функция, без
/// вызовов ОС: ее проверяют тесты на любой ОС. Разрешения — с весом 15, закрытие DNS мимо туннеля —
/// 10, локальная сеть — 5, «закрыть все» — 0: решает фильтр с наибольшим весом из подошедших.
/// Пересылка (хот-спот, общий доступ, ВМ и контейнеры с NAT), пока адаптера туннеля нет, закрыта вся, кроме
/// локальной сети по настройке: на слое пересылки нет портов и программ, поэтому ни сервера, ни DNS там нет. С
/// адаптером туннеля фильтров пересылки нет: ее ведут в туннель маршруты sing-box.
/// </summary>
public static class WfpRules
{
    public const uint LoopbackFlag = 0x1;

    /// <summary>
    /// Пакеты самой машины на слое пересылки (слабая модель хоста: пришел на один адаптер, адрес — другого): их уже
    /// решили слои установки соединения.
    /// </summary>
    public const uint OutboundPassThruFlag = 0x40000;

    public const uint InboundPassThruFlag = 0x80000;
    public const byte Tcp = 6;
    public const byte Udp = 17;
    public const byte IcmpV6 = 58;

    /// <summary>Сколько адресов сервера можно пропустить — у ключей фильтров есть предел.</summary>
    public const int MaxServers = KillSwitchPolicy.MaxServers;

    /// <summary>Порты запросов имен мимо туннеля: DNS, NetBIOS (имена и датаграммы), DoT, LLMNR.</summary>
    private static readonly ulong[] NamePorts = [53, 137, 138, 853, 5355];

    /// <summary>Ключ фильтра по порядку: базовый GUID, номер — в последних двух байтах.</summary>
    public static Guid FilterKey(int index) =>
        new(0xc9f98d15, 0x2e6c, 0x4db0, 0xaa, 0x5c, 0x48, 0xa6, 0x23, 0xd1, (byte)(index >> 8), (byte)index);

    /// <param name="policy">Что пропускать.</param>
    /// <param name="tunnelLuid">LUID адаптера туннеля; null — адаптера еще нет, разрешение добавится, когда он появится.</param>
    public static IReadOnlyList<WfpRule> For(KillSwitchPolicy policy, ulong? tunnelLuid)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Servers.Count > MaxServers)
        {
            throw new ArgumentException($"Адресов сервера больше {MaxServers}", nameof(policy));
        }

        var rules = new List<WfpRule>();
        foreach (var family in new[] { WfpFamily.V4, WfpFamily.V6 })
        {
            rules.Add(new("loopback", family, true, 15, [new(WfpField.Flags, LoopbackFlag)]));
            if (tunnelLuid is { } luid)
            {
                rules.Add(new("tunnel", family, true, 15, [new(WfpField.LocalInterface, luid)]));
            }

            // Раздельный режим: sing-box отправляет мимо туннеля только то, что выбрал пользователь, — ему можно все, и DNS.
            if (policy.Direct is { } direct)
            {
                rules.Add(new("direct", family, true, 15, [new(WfpField.App, Program: direct.Program)]));
            }
        }

        foreach (var server in policy.Servers)
        {
            if (server.Port is <= 0 or > 65535)
            {
                throw new ArgumentException("Недопустимый порт сервера: " + server.Port, nameof(policy));
            }

            var family = server.Address.AddressFamily == AddressFamily.InterNetworkV6 ? WfpFamily.V6 : WfpFamily.V4;
            rules.Add(new("server", family, true, 15,
                [new(WfpField.RemoteAddress, Address: server.Address), new(WfpField.RemotePort, (ulong)server.Port), .. TcpOrUdp()]));
        }

        // Адрес от DHCP и соседи IPv6 (с объявлениями роутера — без них нет адреса IPv6): данные через них наружу не уходят.
        rules.Add(new("dhcp", WfpFamily.V4, true, 15, [new(WfpField.Protocol, Udp), new(WfpField.LocalPort, 68), new(WfpField.RemotePort, 67)]));
        // Машина как DHCP-сервер хот-спота, общего доступа и ВМ: адрес клиенту — только в локальной сети, наружу не уходит.
        rules.Add(new("dhcp-server", WfpFamily.V4, true, 15,
            [new(WfpField.Protocol, Udp), new(WfpField.LocalPort, 67), new(WfpField.RemotePort, 68), Net("0.0.0.0", 32), Net("255.255.255.255", 32),
                .. PrivateV4]));
        rules.Add(new("dhcp", WfpFamily.V6, true, 15, [new(WfpField.Protocol, Udp), new(WfpField.LocalPort, 546), new(WfpField.RemotePort, 547)]));
        rules.Add(new("neighbors", WfpFamily.V6, true, 15,
            [new(WfpField.Protocol, IcmpV6), new(WfpField.IcmpType, 133), new(WfpField.IcmpType, 134), new(WfpField.IcmpType, 135), new(WfpField.IcmpType, 136)]));
        foreach (var family in new[] { WfpFamily.V4, WfpFamily.V6 })
        {
            if (policy.BlockOutsideDns)
            {
                // Тяжелее локальной сети: DNS роутера мимо туннеля — утечка, даже если локальная сеть разрешена. И запросы имен
                // LLMNR (5355) и NetBIOS (137, 138) — имя, не найденное через DNS, Windows спрашивает у всей сети (Responder).
                rules.Add(new("dns", family, false, 10, [.. NamePorts.Select(port => new WfpCondition(WfpField.RemotePort, port)), .. TcpOrUdp()]));
            }

            if (family == WfpFamily.V6 && policy.Ipv6Outside)
            {
                // «Блокировать IPv6 вне туннеля» выключено при выключенном блоке: IPv6 мимо туннеля — как выбрал пользователь.
                rules.Add(new("ipv6", family, true, 5, []));
            }

            if (policy.AllowLan)
            {
                rules.Add(new("lan", family, true, 5, family == WfpFamily.V4 ? PrivateV4 : PrivateV6));
                if (policy.LanDiscovery)
                {
                    rules.Add(new("discovery", family, true, 5, family == WfpFamily.V4 ? DiscoveryV4 : DiscoveryV6));
                }
            }

            rules.Add(new("block", family, false, 0, []));
        }

        if (tunnelLuid is null)
        {
            // Пересылка без туннеля: пакеты самой машины решены выше, локальная сеть — по настройке, по адресу назначения в обе
            // стороны (и ответ клиенту хот-спота: его адрес частный), остальное закрыто.
            foreach (var family in new[] { WfpFamily.V4, WfpFamily.V6 })
            {
                rules.Add(new("pass-through", family, true, 15,
                    [new(WfpField.Flags, OutboundPassThruFlag), new(WfpField.Flags, InboundPassThruFlag)], WfpHook.Forward));
                if (policy.AllowLan)
                {
                    rules.Add(new("lan", family, true, 5, Destination(family == WfpFamily.V4 ? PrivateV4 : PrivateV6), WfpHook.Forward));
                    if (policy.LanDiscovery)
                    {
                        rules.Add(new("discovery", family, true, 5, Destination(family == WfpFamily.V4 ? DiscoveryV4 : DiscoveryV6), WfpHook.Forward));
                    }
                }

                rules.Add(new("block", family, false, 0, [], WfpHook.Forward));
            }
        }

        return rules;
    }

    private static WfpCondition[] PrivateV4 => [Net("10.0.0.0", 8), Net("172.16.0.0", 12), Net("192.168.0.0", 16), Net("169.254.0.0", 16)];

    private static WfpCondition[] PrivateV6 => [Net("fc00::", 7), Net("fe80::", 10)];

    private static WfpCondition[] DiscoveryV4 => [Net("224.0.0.0", 4), Net("255.255.255.255", 32)];

    private static WfpCondition[] DiscoveryV6 => [Net("ff00::", 8)];

    private static WfpCondition[] Destination(WfpCondition[] networks) => [.. networks.Select(n => n with { Field = WfpField.DestinationAddress })];

    private static WfpCondition[] TcpOrUdp() => [new(WfpField.Protocol, Tcp), new(WfpField.Protocol, Udp)];

    private static WfpCondition Net(string address, int prefix) => new(WfpField.RemoteAddress, Address: IPAddress.Parse(address), PrefixLength: prefix);
}
