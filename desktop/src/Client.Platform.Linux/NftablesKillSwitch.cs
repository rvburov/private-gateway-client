using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Блок при обрыве на Linux — своя таблица nftables <c>inet private_gateway</c>: цепочки output (трафик
/// самой машины) и forward (контейнеры и виртуальные машины со своим NAT: Docker, libvirt) с политикой
/// drop и разрешениями из <see cref="KillSwitchPolicy"/>. Своя таблица не трогает правила других программ
/// (ufw, firewalld, docker) и снимается целиком; блок только сужает: наш drop чужим accept не отменить, а
/// наш accept не отменяет чужой drop. Замена правил — одной транзакцией <c>nft -f -</c>: щели между
/// старыми и новыми нет.
/// </summary>
/// <param name="nft">Вызов nft: аргументы и stdin → вывод; не вышло — <see cref="IOException"/> с текстом nft.</param>
public sealed class NftablesKillSwitch(Func<IReadOnlyList<string>, string?, string> nft) : IKillSwitch
{
    public const string Table = "private_gateway";

    private static readonly string[] Programs = ["/usr/sbin/nft", "/sbin/nft", "/usr/bin/nft"];

    public string Name => "nftables";

    public bool IsEnabled => nft(["list", "tables"], null)
        .Split('\n', StringSplitOptions.TrimEntries).Contains("table inet " + Table, StringComparer.Ordinal);

    /// <summary>nft этой машины — по полному пути, без поиска в PATH: служба работает от root. Нет nft — null.</summary>
    public static NftablesKillSwitch? ForThisMachine() =>
        Programs.FirstOrDefault(File.Exists) is { } program ? new NftablesKillSwitch((arguments, input) => Run(program, arguments, input)) : null;

    public void Enable(KillSwitchPolicy policy) => nft(["-f", "-"], Ruleset(policy));

    // Пустое объявление таблицы перед удалением: таблицы нет — удаление не ошибка.
    public void Disable() => nft(["-f", "-"], $"table inet {Table}\ndelete table inet {Table}\n");

    /// <summary>Правила для <c>nft -f -</c>: прежняя таблица удаляется и ставится новая в одной транзакции.</summary>
    public static string Ruleset(KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rules = new List<string> { "oifname \"lo\" accept" };

        // Перенаправленное — в туннель и из него (ответы sing-box контейнерам), остальное — как у машины:
        // DNS мимо туннеля закрыт, локальная сеть по настройке. Сервера здесь нет: к нему ходит только служба.
        var forward = new List<string>();
        if (policy.TunnelInterface is { } tunnel)
        {
            // Имя попадает в текст правил — только буквы, цифры, «_», «.», «-» и не длиннее 15 знаков (IFNAMSIZ).
            if (tunnel.Length is 0 or > 15 || !tunnel.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                throw new ArgumentException("Недопустимое имя адаптера туннеля: " + tunnel, nameof(policy));
            }

            rules.Add($"oifname \"{tunnel}\" accept");
            forward.Add($"iifname \"{tunnel}\" accept");
            forward.Add($"oifname \"{tunnel}\" accept");
        }

        // Раздельный режим: выход sing-box по правилам обхода помечен — его пакеты идут мимо туннеля, DNS тоже.
        if (policy.Direct is { } direct)
        {
            rules.Add($"meta mark {direct.Mark.ToString(CultureInfo.InvariantCulture)} accept");
        }

        foreach (var server in policy.Servers)
        {
            if (server.Port is <= 0 or > 65535)
            {
                throw new ArgumentException("Недопустимый порт сервера: " + server.Port.ToString(CultureInfo.InvariantCulture), nameof(policy));
            }

            var family = server.Address.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip";
            rules.Add($"{family} daddr {server.Address} meta l4proto {{ tcp, udp }} th dport {server.Port.ToString(CultureInfo.InvariantCulture)} accept");
        }

        // Свой DNS в частной сети: его выбрал пользователь, и Xray спрашивает его напрямую — раньше закрытия DNS.
        if (policy.LanDns is { } lanDns)
        {
            var family = lanDns.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip";
            rules.Add($"{family} daddr {lanDns} meta l4proto {{ tcp, udp }} th dport 53 accept");
        }

        // Адрес от DHCP и соседи IPv6: без них машина теряет сеть, данные через них наружу не уходят.
        rules.Add("udp sport 68 udp dport 67 accept");
        // Машина как DHCP-сервер ВМ и хот-спота (libvirt, NetworkManager): адрес клиенту — только в локальной сети.
        rules.Add("ip daddr { 255.255.255.255, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } udp sport 67 udp dport 68 accept");
        rules.Add("udp sport 546 udp dport 547 accept");
        rules.Add("icmpv6 type { nd-router-solicit, nd-neighbor-solicit, nd-neighbor-advert } accept");
        var redirect = DnsRedirect(policy);
        if (redirect is not null)
        {
            // Перенаправленный запрос (цепочка dns): фильтр после nat видит прежний интерфейс, а не туннель.
            rules.Add($"ip daddr {policy.TunnelDns} meta l4proto {{ tcp, udp }} th dport 53 accept");
        }

        var shared = new List<string>();
        if (policy.BlockOutsideDns)
        {
            // Раньше локальной сети: DNS роутера мимо туннеля — утечка, даже если локальная сеть разрешена. И запросы имен
            // LLMNR (5355) и NetBIOS (137, 138): ими Windows и Samba спрашивают имя, не найденное через DNS, всю сеть —
            // имена уходят в чужой Wi-Fi (Responder). mDNS (5353) — у «Обнаружения устройств».
            shared.Add("meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop");
        }

        // «Блокировать IPv6 вне туннеля» выключено при выключенном блоке: IPv6 мимо туннеля — как выбрал пользователь.
        if (policy.Ipv6Outside)
        {
            shared.Add("meta nfproto ipv6 accept");
        }

        // Для перенаправленного — по адресу назначения в обе стороны: и контейнер в локальную сеть, и ответ
        // ему (адрес контейнера частный). Наружу мимо туннеля адрес назначения не частный — закрыто.
        if (policy.AllowLan)
        {
            shared.Add("ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } accept");
            shared.Add("ip6 daddr { fc00::/7, fe80::/10 } accept");
            if (policy.LanDiscovery)
            {
                shared.Add("ip daddr { 224.0.0.0/4, 255.255.255.255 } accept");
                shared.Add("ip6 daddr ff00::/8 accept");
            }
        }

        rules.AddRange(shared);
        forward.AddRange(shared);

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"table inet {Table}\n")
            .Append(CultureInfo.InvariantCulture, $"delete table inet {Table}\n")
            .Append(CultureInfo.InvariantCulture, $"table inet {Table} {{\n");
        Chain(text, "output", rules);
        Chain(text, "forward", forward);

        if (redirect is not null)
        {
            Chain(text, "dns", [redirect], "type nat hook output priority -100; policy accept;");
        }

        return text.Append("}\n").ToString();
    }

    /// <summary>
    /// DNS к разрешенной локальной сети (роутер из resolv.conf без systemd-resolved) идет мимо туннеля, а там закрыт —
    /// перенаправить его в адрес DNS туннеля, его отвечает Xray. Раньше фильтра: nat — выше по приоритету. Выход
    /// sing-box мимо туннеля (метка) — как есть. Не нужно — null.
    /// </summary>
    private static string? DnsRedirect(KillSwitchPolicy policy)
    {
        if (policy is not { TunnelInterface: { } name, TunnelDns: { AddressFamily: AddressFamily.InterNetwork } dns, BlockOutsideDns: true, AllowLan: true })
        {
            return null;
        }

        var mark = policy.Direct is { } exit ? $"meta mark != {exit.Mark.ToString(CultureInfo.InvariantCulture)} " : string.Empty;
        // Свой DNS в частной сети — как есть: его спрашивает сам Xray, перенаправленный, запрос вернулся бы в Xray по кругу.
        var own = policy.LanDns is { AddressFamily: AddressFamily.InterNetwork } lanDns ? $"ip daddr != {lanDns} " : string.Empty;
        return $"oifname != \"{name}\" {mark}ip daddr {{ 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }} {own}"
            + $"meta l4proto {{ tcp, udp }} th dport 53 dnat ip to {dns}";
    }

    private static void Chain(StringBuilder text, string hook, List<string> rules, string? type = null)
    {
        text.Append(CultureInfo.InvariantCulture, $"\tchain {hook} {{\n")
            .Append(CultureInfo.InvariantCulture, $"\t\t{type ?? $"type filter hook {hook} priority 0; policy drop;"}\n");
        foreach (var rule in rules)
        {
            text.Append("\t\t").Append(rule).Append('\n');
        }

        text.Append("\t}\n");
    }

    private static string Run(string program, IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("nft не запустился");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input ?? string.Empty);
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : throw new IOException("nft: " + error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("nft не запустился: " + e.Message, e);
        }
    }
}
