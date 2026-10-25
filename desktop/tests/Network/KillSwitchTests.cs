using System.Net;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Блок при обрыве на Linux: текст правил nftables и сами правила в своем сетевом пространстве с
/// двумя парами veth к сети машины — обычная сеть и «туннель». Правила машины тест не трогает.
/// </summary>
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
            "udp sport 546 udp dport 547 accept",
            "icmpv6 type { nd-router-solicit, nd-neighbor-solicit, nd-neighbor-advert } accept",
            "meta l4proto { tcp, udp } th dport { 53, 853 } drop",
            "}",
            "}",
        ], "прежняя таблица удаляется и новая ставится одной транзакцией; локальная сеть по умолчанию закрыта");
    }

    [Fact]
    public void Local_network_and_dns_follow_the_settings()
    {
        var lan = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true)));
        lan.ShouldContain("ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } accept");
        lan.ShouldContain("ip6 daddr { fc00::/7, fe80::/10 } accept");
        lan.IndexOf("meta l4proto { tcp, udp } th dport { 53, 853 } drop").ShouldBeLessThan(lan.FindIndex(l => l.StartsWith("ip daddr { 10.", StringComparison.Ordinal)),
            "DNS роутера закрыт раньше, чем открыта локальная сеть");
        lan.ShouldNotContain(l => l.Contains("224.0.0.0/4", StringComparison.Ordinal), "обнаружение устройств — отдельной настройкой");
        lan.ShouldNotContain(l => l.StartsWith("oifname", StringComparison.Ordinal) && !l.Contains("\"lo\"", StringComparison.Ordinal), "туннеля еще нет");

        var discovery = Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true, LanDiscovery: true, BlockOutsideDns: false)));
        discovery.ShouldContain("ip daddr { 224.0.0.0/4, 255.255.255.255 } accept");
        discovery.ShouldContain("ip6 daddr ff00::/8 accept");
        discovery.ShouldNotContain(l => l.Contains("dport { 53, 853 }", StringComparison.Ordinal));

        Lines(NftablesKillSwitch.Ruleset(new KillSwitchPolicy(null, [], LanDiscovery: true)))
            .ShouldNotContain(l => l.Contains("224.0.0.0/4", StringComparison.Ordinal), "без локальной сети нет и обнаружения устройств");
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
        var octet = Random.Shared.Next(1, 250);
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

    private static List<string> Lines(string rules) => [.. rules.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
}
