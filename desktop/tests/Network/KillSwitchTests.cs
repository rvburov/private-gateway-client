using System.Net;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Блок при обрыве на Linux: текст правил nftables и сами правила в своем сетевом пространстве с
/// двумя парами veth к сети машины — обычная сеть и «туннель»; пересланный трафик — из «контейнера» в
/// соседнем пространстве. Правила машины тест не трогает.
/// </summary>
[Collection(NamespaceSerial.Name)]
public sealed class KillSwitchTests
{
    private static readonly IPAddress Server = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void Rules_close_everything_but_the_tunnel_the_server_and_the_network_basics()
    {
        var rules = NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [new(Server, 443), new(IPAddress.Parse("2001:db8::20"), 8443)]));

        Lines(rules).ShouldBe([
            "table inet private_gateway",
            "delete table inet private_gateway",
            "table inet private_gateway {",
            "chain output {",
            "type filter hook output priority 0; policy drop;",
            "oifname \"lo\" accept",
            "oifname \"pgtun0\" accept",
            "ip daddr 198.51.100.20 meta l4proto { tcp, udp } th dport 443 accept",
            "ip6 daddr 2001:db8::20 meta l4proto { tcp, udp } th dport 8443 accept",
            "udp sport 68 udp dport 67 accept",
            "ip daddr { 255.255.255.255, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } udp sport 67 udp dport 68 accept",
            "udp sport 546 udp dport 547 accept",
            "icmpv6 type { nd-router-solicit, nd-neighbor-solicit, nd-neighbor-advert } accept",
            "meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop",
            "}",
            "chain forward {",
            "type filter hook forward priority 0; policy drop;",
            "iifname \"pgtun0\" accept",
            "oifname \"pgtun0\" accept",
            "meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop",
            "}",
            "}",
        ], "прежняя таблица удаляется и новая ставится одной транзакцией; локальная сеть по умолчанию закрыта; " +
            "пересланное (контейнеры, виртуальные машины) — только через туннель, к серверу мимо него ходит одна служба");
    }

    [Fact]
    public void Split_mode_exit_passes_by_its_mark_before_the_dns_rule()
    {
        var lines = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [new(Server, 443)], Direct: new DirectExit("/opt/pg/sing-box", 0x5047))));

        lines.ShouldContain("meta mark 20551 accept");
        lines.IndexOf("meta mark 20551 accept").ShouldBe(lines.IndexOf("oifname \"pgtun0\" accept") + 1,
            "обход выбран пользователем: его DNS тоже идет напрямую — правило раньше закрытия DNS");
        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", []))).ShouldNotContain(l => l.StartsWith("meta mark", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_network_and_dns_follow_the_settings()
    {
        var lan = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true)));
        lan.ShouldContain("ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } accept");
        lan.ShouldContain("ip6 daddr { fc00::/7, fe80::/10 } accept");
        lan.IndexOf("meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop").ShouldBeLessThan(lan.FindIndex(l => l.StartsWith("ip daddr { 10.", StringComparison.Ordinal)),
            "DNS роутера закрыт раньше, чем открыта локальная сеть");
        lan.ShouldNotContain(l => l.Contains("224.0.0.0/4", StringComparison.Ordinal), "обнаружение устройств — отдельной настройкой");
        lan.ShouldNotContain(l => l.StartsWith("oifname", StringComparison.Ordinal) && !l.Contains("\"lo\"", StringComparison.Ordinal), "туннеля еще нет");

        var discovery = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true, LanDiscovery: true, BlockOutsideDns: false)));
        discovery.ShouldContain("ip daddr { 224.0.0.0/4, 255.255.255.255 } accept");
        discovery.ShouldContain("ip6 daddr ff00::/8 accept");
        discovery.ShouldNotContain(l => l.Contains("dport { 53, 137", StringComparison.Ordinal));

        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], LanDiscovery: true)))
            .ShouldNotContain(l => l.Contains("224.0.0.0/4", StringComparison.Ordinal), "без локальной сети нет и обнаружения устройств");
    }

    [Fact]
    public void Dns_to_the_allowed_local_network_is_redirected_into_the_tunnel_only_while_it_is_up()
    {
        var dns = IPAddress.Parse("172.19.0.2");
        var lines = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [new(Server, 443)], AllowLan: true,
            Direct: new DirectExit("/opt/pg/sing-box", 0x5047), TunnelDns: dns)));

        lines.Skip(lines.IndexOf("chain dns {")).Take(8).ShouldBe([
            "chain dns {",
            "type nat hook output priority -100; policy accept;",
            "oifname != \"pgtun0\" oifname != \"lo\" meta mark != 20551 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } "
                + "meta l4proto { tcp, udp } th dport 53 dnat ip to 172.19.0.2",
            "}",
            "chain dns_out {",
            "type filter hook postrouting priority 0; policy accept;",
            "oifname != \"pgtun0\" ip daddr 172.19.0.2 meta l4proto { tcp, udp } th dport 53 drop",
            "}",
        ], "DNS роутера (resolv.conf без systemd-resolved) — в туннель, выход sing-box мимо туннеля и свой адрес машины — как есть; "
            + "перенаправленное, что после маршрутизации ушло не в туннель (сокет с привязкой к карте), — закрыто");
        lines.IndexOf("ip daddr 172.19.0.2 meta l4proto { tcp, udp } th dport 53 accept")
            .ShouldBeLessThan(lines.IndexOf("meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop"),
                "фильтр после nat видит прежний интерфейс — перенаправленное разрешено раньше закрытия DNS");

        foreach (var policy in new KillSwitchPolicy[]
        {
            new(null, [], AllowLan: true, TunnelDns: dns),
            new("pgtun0", [], AllowLan: false, TunnelDns: dns),
            new("pgtun0", [], AllowLan: true, BlockOutsideDns: false, TunnelDns: dns),
            new("pgtun0", [], AllowLan: true),
        })
        {
            Lines(NftablesKillSwitch.Ruleset(policy)).ShouldNotContain(l => l.Contains("172.19.0.2", StringComparison.Ordinal),
                $"{policy}: туннеля нет, локальная сеть закрыта (DNS к ней и так идет в туннель) или DNS роутера разрешен");
        }
    }

    [Fact]
    public void Ipv6_outside_the_tunnel_is_open_only_when_the_policy_says_so()
    {
        var open = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [], Ipv6Outside: true)));

        open.IndexOf("meta nfproto ipv6 accept").ShouldBeGreaterThan(open.IndexOf("meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop"),
            "«Блокировать IPv6 вне туннеля» выключено при выключенном блоке — IPv6 мимо туннеля открыт, но запросы имен и по IPv6 закрыты");
        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", []))).ShouldNotContain("meta nfproto ipv6 accept");
    }

    [Fact]
    public void Own_dns_in_a_private_network_gets_dns_alone_and_is_not_redirected()
    {
        var lines = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [new(Server, 443)], AllowLan: true,
            TunnelDns: IPAddress.Parse("172.19.0.2"), LanDns: IPAddress.Parse("192.168.1.53"))));

        lines.IndexOf("ip daddr 192.168.1.53 meta l4proto { tcp, udp } th dport 53 accept")
            .ShouldBeLessThan(lines.IndexOf("meta l4proto { tcp, udp } th dport { 53, 137, 138, 853, 5355 } drop"), "раньше закрытия DNS");
        lines.ShouldContain("oifname != \"pgtun0\" oifname != \"lo\" ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } ip daddr != 192.168.1.53 "
            + "meta l4proto { tcp, udp } th dport 53 dnat ip to 172.19.0.2", "запрос самого Xray к своему DNS в туннель не возвращается");
        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [], LanDns: IPAddress.Parse("fd00::53"))))
            .ShouldContain("ip6 daddr fd00::53 meta l4proto { tcp, udp } th dport 53 accept");
        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [], AllowLan: true)))
            .ShouldNotContain(l => l.Contains("192.168.1.53", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_of_the_service_passes_only_its_marked_socket_to_the_doh_address()
    {
        var lookup = new ServiceLookup("/usr/lib/private-gateway/PrivateGateway.Service", 0x5048, [new(IPAddress.Parse("1.1.1.1"), 443)]);
        var lines = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [new(Server, 443)], Lookup: lookup)));

        lines.ShouldContain("meta mark 20552 ip daddr 1.1.1.1 tcp dport 443 accept");
        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [new(Server, 443)]))).ShouldNotContain(l => l.Contains("20552", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pg tun")]
    [InlineData("pgtun0\" accept; flush ruleset; #")]
    [InlineData("")]
    [InlineData("sixteen-letters0")]
    public void Strange_interface_names_do_not_reach_the_rules(string name) =>
        Should.Throw<ArgumentException>(() => NftablesKillSwitch.Ruleset(new KillSwitchPolicy(name, [])));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Ports_outside_the_range_do_not_reach_the_rules(int port) =>
        Should.Throw<ArgumentException>(() => NftablesKillSwitch.Ruleset(new KillSwitchPolicy("pgtun0", [new(Server, port)])));

    [Fact]
    public void Switch_hands_the_rules_to_nft_and_reads_its_tables()
    {
        var calls = new List<(string Arguments, string? Input)>();
        var tables = "table inet filter\ntable inet private_gateway2\n";
        var killSwitch = new NftablesKillSwitch((arguments, input) =>
        {
            calls.Add((string.Join(' ', arguments), input));
            return arguments[0] == "list" ? tables : string.Empty;
        });
        var policy = new KillSwitchPolicy("pgtun0", [new(Server, 443)]);

        killSwitch.IsEnabled.ShouldBeFalse("чужая таблица с похожим именем — не наша");
        tables += "table inet private_gateway\n";
        killSwitch.IsEnabled.ShouldBeTrue();
        killSwitch.Enable(policy);
        killSwitch.Disable();

        calls[^2].ShouldBe(("-f -", NftablesKillSwitch.Ruleset(policy)));
        calls[^1].ShouldBe(("-f -", "table inet private_gateway\ndelete table inet private_gateway\n"), "снять можно и то, чего нет");
    }

    [Fact]
    public async Task Rules_in_a_namespace_let_through_only_what_the_policy_allows()
    {
        NetworkNamespace.SkipUnlessAvailable("nft", "ip", "curl", "python3");
        using var netns = NetworkNamespace.Create();
        var octet = netns.Octet;
        var host = IPAddress.Parse($"10.254.{octet}.1");
        var tunnelHost = IPAddress.Parse($"10.253.{octet}.1");
        netns.Link("pgnet0", host, IPAddress.Parse($"10.254.{octet}.2"));
        netns.Link("pgtun0", tunnelHost, IPAddress.Parse($"10.253.{octet}.2"));
        using var server = new LocalSite("сервер", host);
        using var lan = new LocalSite("локальная сеть", host);
        using var tunnel = new LocalSite("туннель", tunnelHost);
        using var dns = new TestDns(IPAddress.Parse("1.1.1.1"), new IPEndPoint(host, 53));
        var killSwitch = new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments]));
        (await netns.CurlAsync(lan.Url)).ShouldBe("локальная сеть", "без блока сеть открыта");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [new(host, server.Url.Port)]));

        killSwitch.IsEnabled.ShouldBeTrue();
        (await netns.CurlAsync(server.Url)).ShouldBe("сервер", "к серверу — мимо туннеля");
        (await netns.CurlAsync(tunnel.Url)).ShouldBe("туннель", "через адаптер туннеля — все");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3), "другой порт того же адреса — не сервер");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.ResolveAsync("example.com", host, seconds: 3));

        await Should.ThrowAsync<InvalidOperationException>(() => netns.FetchAsync(lan.Url, 0x5047, seconds: 3), "без раздельного режима метка не помогает");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [new(host, server.Url.Port)], Direct: new DirectExit("/opt/pg/sing-box", 0x5047)));

        (await netns.FetchAsync(lan.Url, 0x5047)).ShouldBe("локальная сеть", "выход sing-box мимо туннеля узнается по метке");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url, seconds: 3), "другие программы этим путем не пройдут");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.FetchAsync(lan.Url, 0x5048, seconds: 3), "чужая метка — не наш выход");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [], AllowLan: true));

        (await netns.CurlAsync(lan.Url)).ShouldBe("локальная сеть", "локальная сеть разрешена");
        await Should.ThrowAsync<InvalidOperationException>(() => netns.ResolveAsync("example.com", host, seconds: 3),
            "DNS роутера мимо туннеля закрыт и при разрешенной локальной сети");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [], AllowLan: true, BlockOutsideDns: false));

        (await netns.ResolveAsync("example.com", host)).ShouldBe("1.1.1.1");
        netns.Run(null, "nft", "list", "tables").Split('\n').Count(l => l.Contains(NftablesKillSwitch.Table, StringComparison.Ordinal))
            .ShouldBe(1, "правила заменяются, а не копятся");

        killSwitch.Disable();
        killSwitch.Disable();

        killSwitch.IsEnabled.ShouldBeFalse();
        (await netns.ResolveAsync("example.com", host)).ShouldBe("1.1.1.1");
        dns.Queries.Count.ShouldBe(2, "закрытые запросы до DNS не дошли");
    }

    [Fact]
    public async Task Llmnr_and_netbios_stay_closed_with_the_local_network_and_discovery_while_mdns_goes()
    {
        NetworkNamespace.SkipUnlessAvailable("nft", "ip", "python3");
        using var netns = NetworkNamespace.Create();
        var octet = netns.Octet;
        var host = IPAddress.Parse($"10.254.{octet}.1");
        var self = IPAddress.Parse($"10.254.{octet}.2");
        netns.Link("pgnet0", host, self);
        var killSwitch = new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments]));

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [], AllowLan: true, LanDiscovery: true));

        (await netns.SendUdpAsync(IPAddress.Parse("224.0.0.251"), 5353, self)).ShouldBe("sent", "mDNS — у «Обнаружения устройств» (принтеры, AirPlay)");
        (await netns.SendUdpAsync(host, 9, self)).ShouldBe("sent", "локальная сеть разрешена");
        foreach (var (address, port) in new[] { ("224.0.0.252", 5355), ("10.254.0.0", 137), ("10.254.0.0", 138) })
        {
            var to = address == "10.254.0.0" ? host : IPAddress.Parse(address);
            await Should.ThrowAsync<InvalidOperationException>(() => netns.SendUdpAsync(to, port, self),
                $"{to}:{port} — запрос имени мимо туннеля (LLMNR, NetBIOS) закрыт и при разрешенной локальной сети");
        }

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [], AllowLan: true, LanDiscovery: true, BlockOutsideDns: false));

        (await netns.SendUdpAsync(IPAddress.Parse("224.0.0.252"), 5355, self)).ShouldBe("sent", "«Блокировать DNS провайдера» выключено — LLMNR открыт");
    }

    [Fact]
    public async Task Machine_answers_as_a_dhcp_server_only_into_the_local_network()
    {
        NetworkNamespace.SkipUnlessAvailable("nft", "ip", "python3");
        using var netns = NetworkNamespace.Create();
        var (host, _, @public) = netns.LinkAsMachine();
        var killSwitch = new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments]));

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", []));

        (await ReplyAsync(host)).ShouldBe("sent", "ответ DHCP клиенту ВМ или хот-спота — в локальную сеть, без него у них нет адреса");
        (await ReplyAsync(@public)).ShouldBe("blocked", "с порта 67 наружу — нет");

        // Ответ DHCP-сервера: с порта 67 на порт 68 адреса; отброшенный пакет python видит сразу ошибкой EPERM.
        Task<string> ReplyAsync(IPAddress to) => netns.RunAsync("python3", "-c", """
            import socket, sys
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.bind(("0.0.0.0", 67))
            try:
                s.sendto(b"offer", (sys.argv[1], 68))
                print("sent", end="")
            except PermissionError:
                print("blocked", end="")
            """, to.ToString());
    }

    [Fact]
    public async Task Forwarded_traffic_of_containers_goes_only_into_the_tunnel_and_the_allowed_local_network()
    {
        NetworkNamespace.SkipUnlessAvailable("nft", "ip", "curl", "sysctl");
        using var machine = NetworkNamespace.Create();
        using var container = NetworkNamespace.Create();
        var octet = machine.Octet;
        var host = IPAddress.Parse($"10.254.{octet}.1");
        var tunnelHost = IPAddress.Parse($"10.253.{octet}.1");
        machine.Link("pgnet0", host, IPAddress.Parse($"10.254.{octet}.2"));
        machine.Link("pgtun0", tunnelHost, IPAddress.Parse($"10.253.{octet}.2"));

        // «Контейнер» за машиной, как у Docker: своя сеть, машина пересылает его пакеты и подменяет адрес (NAT).
        machine.Run(null, "ip", "link", "add", "pgbr0", "type", "veth", "peer", "name", "eth0", "netns", container.Name);
        machine.Run(null, "ip", "addr", "add", $"10.251.{octet}.1/30", "dev", "pgbr0");
        machine.Run(null, "ip", "link", "set", "pgbr0", "up");
        container.Run(null, "ip", "addr", "add", $"10.251.{octet}.2/30", "dev", "eth0");
        container.Run(null, "ip", "link", "set", "eth0", "up");
        container.Run(null, "ip", "route", "add", "default", "via", $"10.251.{octet}.1");
        machine.Run(null, "sysctl", "-qw", "net.ipv4.ip_forward=1");
        machine.Run("table ip pgtest_nat {\n\tchain post {\n\t\ttype nat hook postrouting priority 100;\n\t\toifname != \"pgbr0\" masquerade\n\t}\n}\n", "nft", "-f", "-");
        using var server = new LocalSite("сервер", host);
        using var lan = new LocalSite("локальная сеть", host);
        using var tunnel = new LocalSite("туннель", tunnelHost);
        var killSwitch = new NftablesKillSwitch((arguments, input) => machine.Run(input, ["nft", .. arguments]));
        (await container.CurlAsync(lan.Url)).ShouldBe("локальная сеть", "без блока пересылка открыта");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [new(host, server.Url.Port)]));

        (await container.CurlAsync(tunnel.Url)).ShouldBe("туннель", "пересланное в туннель — проходит, ответ из туннеля — тоже");
        await Should.ThrowAsync<InvalidOperationException>(() => container.CurlAsync(lan.Url, seconds: 3), "мимо туннеля — закрыто");
        await Should.ThrowAsync<InvalidOperationException>(() => container.CurlAsync(server.Url, seconds: 3), "к серверу мимо туннеля ходит только служба");
        (await machine.CurlAsync(server.Url)).ShouldBe("сервер", "сама машина к серверу — по-прежнему");

        killSwitch.Enable(new KillSwitchPolicy("pgtun0", [], AllowLan: true));

        (await container.CurlAsync(lan.Url)).ShouldBe("локальная сеть", "разрешенная локальная сеть — и контейнерам");

        killSwitch.Disable();

        (await container.CurlAsync(lan.Url)).ShouldBe("локальная сеть", "блок снят — пересылка открыта");
    }

    private static List<string> Lines(string rules) => [.. rules.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}
