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

/// <summary>Поле условия фильтра WFP.</summary>
public enum WfpField
{
    /// <summary>Флаги соединения; значение — <see cref="WfpRules.LoopbackFlag"/>.</summary>
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
public sealed record WfpRule(string Name, WfpFamily Family, bool Permit, byte Weight, IReadOnlyList<WfpCondition> Conditions)
{
    public override string ToString() =>
        $"{Weight} {(Permit ? "permit" : "block")} {Family} {Name}: {string.Join(", ", Conditions)}";
}

/// <summary>
/// Политика блока при обрыве (<see cref="KillSwitchPolicy"/>) в фильтрах WFP — чистая функция, без
/// вызовов ОС: ее проверяют тесты на любой ОС. Разрешения — с весом 15, закрытие DNS мимо туннеля —
/// 10, локальная сеть — 5, «закрыть все» — 0: решает фильтр с наибольшим весом из подошедших.
/// </summary>
public static class WfpRules
{
    public const uint LoopbackFlag = 0x1;
    public const byte Tcp = 6;
    public const byte Udp = 17;
    public const byte IcmpV6 = 58;

    /// <summary>Сколько адресов сервера можно пропустить — у ключей фильтров есть предел.</summary>
    public const int MaxServers = 16;

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
        rules.Add(new("dhcp", WfpFamily.V6, true, 15, [new(WfpField.Protocol, Udp), new(WfpField.LocalPort, 546), new(WfpField.RemotePort, 547)]));
        rules.Add(new("neighbors", WfpFamily.V6, true, 15,
            [new(WfpField.Protocol, IcmpV6), new(WfpField.IcmpType, 133), new(WfpField.IcmpType, 134), new(WfpField.IcmpType, 135), new(WfpField.IcmpType, 136)]));
        foreach (var family in new[] { WfpFamily.V4, WfpFamily.V6 })
        {
            if (policy.BlockOutsideDns)
            {
                // Тяжелее локальной сети: DNS роутера мимо туннеля — утечка, даже если локальная сеть разрешена.
                rules.Add(new("dns", family, false, 10, [new(WfpField.RemotePort, 53), new(WfpField.RemotePort, 853), .. TcpOrUdp()]));
            }

            if (policy.AllowLan)
            {
                rules.Add(new("lan", family, true, 5, family == WfpFamily.V4
                    ? [Net("10.0.0.0", 8), Net("172.16.0.0", 12), Net("192.168.0.0", 16), Net("169.254.0.0", 16)]
                    : [Net("fc00::", 7), Net("fe80::", 10)]));
                if (policy.LanDiscovery)
                {
                    rules.Add(new("discovery", family, true, 5, family == WfpFamily.V4
                        ? [Net("224.0.0.0", 4), Net("255.255.255.255", 32)]
                        : [Net("ff00::", 8)]));
                }
            }

            rules.Add(new("block", family, false, 0, []));
        }

        return rules;
    }

    private static WfpCondition[] TcpOrUdp() => [new(WfpField.Protocol, Tcp), new(WfpField.Protocol, Udp)];

    private static WfpCondition Net(string address, int prefix) => new(WfpField.RemoteAddress, Address: IPAddress.Parse(address), PrefixLength: prefix);
}
