using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Конфиг sing-box для TUN режима VPN: весь трафик и DNS — в Xray, сервер — мимо туннеля.</summary>
public sealed class SingBoxConfigTests
{
    private static readonly TunLink Link = new(31000, "user-1", "pass-1", "198.51.100.20");

    [Fact]
    public void Everything_goes_through_xray_and_the_server_goes_around_the_tunnel()
    {
        var config = Build([IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20")]);

        var tun = Tun(config);
        (tun["auto_route"]!.GetValue<bool>(), tun["strict_route"]!.GetValue<bool>(), tun["stack"]!.GetValue<string>())
            .ShouldBe((true, true, "system"));
        Strings(tun["address"]).ShouldBe([SingBoxConfigBuilder.TunAddress], "IPv6 закрыт по умолчанию — у TUN его нет");
        Strings(tun["route_exclude_address"]).ShouldBe(["198.51.100.20/32", "2001:db8::20/128"], "локальная сеть не разрешена — мимо туннеля только сервер");
        tun.ContainsKey("mtu").ShouldBeFalse("MTU подбирает sing-box");
        if (OperatingSystem.IsMacOS())
        {
            tun.ContainsKey("interface_name").ShouldBeFalse("на macOS имя адаптера дает система");
        }
        else
        {
            tun["interface_name"]!.GetValue<string>().ShouldBe(OperatingSystem.IsWindows() ? "Private Gateway" : "pgtun0",
                "по имени адаптера туннель узнают правила блока при обрыве");
        }

        if (OperatingSystem.IsLinux())
        {
            (tun["iproute2_table_index"]!.GetValue<int>(), tun["iproute2_rule_index"]!.GetValue<int>()).ShouldBe((20551, 9047),
                "свои таблица и номера правил: оставшиеся после аварии правила служба убирает по ним, чужие не задевает");
        }
        else
        {
            tun.ContainsKey("iproute2_rule_index").ShouldBeFalse("правила ip rule — только в Linux");
        }

        var xray = config["outbounds"]!.AsArray().ShouldHaveSingleItem()!;
        (xray["type"]!.GetValue<string>(), xray["server"]!.GetValue<string>(), xray["server_port"]!.GetValue<int>()).ShouldBe(("socks", "127.0.0.1", 31000));
        (xray["username"]!.GetValue<string>(), xray["password"]!.GetValue<string>()).ShouldBe(("user-1", "pass-1"));
        var route = config["route"]!.AsObject();
        route["final"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.XrayTag);
        route["auto_detect_interface"]!.GetValue<bool>().ShouldBeTrue();
        route.ContainsKey("rules").ShouldBeFalse("и DNS — в Xray: его отвечает выход DNS Xray, а не sing-box и не провайдер");
        config.ContainsKey("dns").ShouldBeFalse("своего DNS у sing-box нет — имен он не ищет");
        config["log"]!["level"]!.GetValue<string>().ShouldBe("info", "строка «sing-box started» — признак готовности; адреса соединений в журнал не пускает служба");
    }

    [Fact]
    public void Settings_shape_the_tunnel()
    {
        var config = Build([IPAddress.Parse("198.51.100.20")],
            ProtectionSettings.Default with { BlockIpv6 = false, AllowLan = true },
            ConnectionSettings.Default with { Stack = TunEngine.Gvisor, Mtu = 1420, StrictRoutes = false, CoreLog = CoreLogLevel.Debug });

        var tun = Tun(config);
        Strings(tun["address"]).ShouldBe([SingBoxConfigBuilder.TunAddress, SingBoxConfigBuilder.TunAddress6]);
        Strings(tun["route_exclude_address"]).ShouldContain("192.168.0.0/16", "локальная сеть разрешена — мимо туннеля");
        Strings(tun["route_exclude_address"]).ShouldContain("10.0.0.0/8");
        (tun["stack"]!.GetValue<string>(), tun["mtu"]!.GetValue<int>(), tun["strict_route"]!.GetValue<bool>()).ShouldBe(("gvisor", 1420, false));
        config["log"]!["level"]!.GetValue<string>().ShouldBe("debug");

        Tun(Build([IPAddress.Parse("198.51.100.20")], connection: ConnectionSettings.Default with { Stack = TunEngine.Mixed }))["stack"]!
            .GetValue<string>().ShouldBe("mixed");
        Strings(Tun(Build([IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default with { BlockIpv6 = false, Addresses = AddressPreference.Ipv4Only }))["address"])
            .ShouldBe([SingBoxConfigBuilder.TunAddress], "только IPv4 — и у TUN только IPv4");
    }

    [Fact]
    public void Chosen_programs_go_around_the_tunnel_through_a_marked_exit()
    {
        var config = SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default,
            ConnectionSettings.Default, ["/opt/game/game", "/usr/bin/xray"]));

        var direct = config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == SingBoxConfigBuilder.DirectTag)!;
        direct["type"]!.GetValue<string>().ShouldBe("direct");
        if (OperatingSystem.IsLinux())
        {
            direct["routing_mark"]!.GetValue<uint>().ShouldBe(SingBoxConfigBuilder.DirectMark, "по метке выход мимо туннеля пропускает блок при обрыве");
        }
        else
        {
            direct.AsObject().ContainsKey("routing_mark").ShouldBeFalse("метка — только в Linux; WFP узнает выход по программе sing-box");
        }

        var rule = config["route"]!["rules"]!.AsArray().ShouldHaveSingleItem()!;
        Strings(rule["process_path"]).ShouldBe(["/opt/game/game", "/usr/bin/xray"]);
        rule["outbound"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.DirectTag);
        config["route"]!["final"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.XrayTag, "остальное — через Xray и сервер");
    }

    [Fact]
    public void Programs_of_split_mode_and_xray_with_a_direct_exit_go_around_the_tunnel()
    {
        BypassApp[] apps = [new("/opt/game/game", "Игра"), new("/opt/old/old", "Старая", IsEnabled: false)];
        DomainRule[] direct = [new("example.com", RuleAction.Direct)];
        DomainRule[] blockOnly = [new("ads.example.net", RuleAction.Block)];
        var closed = ProtectionSettings.Default;

        SingBoxConfigBuilder.DirectPrograms(new RoutingSettings(RoutingMode.AllTraffic, apps, direct), closed, "/x/xray")
            .ShouldBeEmpty("«Весь трафик», локальная сеть закрыта — мимо туннеля ничего");
        SingBoxConfigBuilder.DirectPrograms(new RoutingSettings(RoutingMode.Split, apps, blockOnly), closed, "/x/xray")
            .ShouldBe(["/opt/game/game"], "выключенная программа идет через VPN; прямого выхода у Xray нет — он в туннеле");
        SingBoxConfigBuilder.DirectPrograms(new RoutingSettings(RoutingMode.Split, [], direct), closed, "/x/xray")
            .ShouldBe(["/x/xray"], "прямой выход Xray для сайтов «Напрямую» не должен вернуться в TUN");
        SingBoxConfigBuilder.DirectPrograms(RoutingSettings.Default, closed with { AllowLan = true }, "/x/xray")
            .ShouldBe(["/x/xray"], "и для локальной сети: не все, что Xray считает локальным, есть в исключениях TUN");
        Build([IPAddress.Parse("198.51.100.20")])["route"]!.AsObject().ContainsKey("rules").ShouldBeFalse();
    }

    [Fact]
    public void Programs_around_the_tunnel_reach_fake_dns_addresses_through_xray()
    {
        BypassApp[] apps = [new("/opt/game/game", "Игра"), new("/opt/old/old", "Старая", IsEnabled: false)];
        var split = new RoutingSettings(RoutingMode.Split, apps, []);
        var fake = ProtectionSettings.Default with { FakeDns = true };

        SingBoxConfigBuilder.FakeIpPrograms(split, fake).ShouldBe(["/opt/game/game"]);
        SingBoxConfigBuilder.FakeIpPrograms(split, ProtectionSettings.Default).ShouldBeEmpty("без FakeDNS программы получают настоящие адреса");
        SingBoxConfigBuilder.FakeIpPrograms(new RoutingSettings(RoutingMode.AllTraffic, apps, []), fake).ShouldBeEmpty();
        SingBoxConfigBuilder.DirectPrograms(split, fake, "/x/xray").ShouldBe(["/opt/game/game", "/x/xray"],
            "Xray соединяет программу напрямую — его выход не должен вернуться в TUN");
        SingBoxConfigBuilder.DirectPrograms(split, ProtectionSettings.Default, "/x/xray").ShouldBe(["/opt/game/game"]);

        var config = SingBoxConfigBuilder.BuildTun(new TunRequest(Link with { BypassPort = 31001 }, [IPAddress.Parse("198.51.100.20")], fake,
            ConnectionSettings.Default, ["/opt/game/game", "/x/xray"], ["/opt/game/game"]));

        var rules = config["route"]!["rules"]!.AsArray();
        rules.Count.ShouldBe(2);
        (string.Join(',', Strings(rules[0]!["process_path"])), string.Join(',', Strings(rules[0]!["ip_cidr"])), rules[0]!["outbound"]!.GetValue<string>())
            .ShouldBe(("/opt/game/game", XrayConfigBuilder.FakeDnsPool, SingBoxConfigBuilder.BypassTag),
                "адрес FakeDNS у программы мимо туннеля — во вход обхода Xray, раньше правила «напрямую»");
        rules[1]!["outbound"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.DirectTag);
        var bypass = config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == SingBoxConfigBuilder.BypassTag)!;
        (bypass["server"]!.GetValue<string>(), bypass["server_port"]!.GetValue<int>(), bypass["username"]!.GetValue<string>(), bypass["password"]!.GetValue<string>())
            .ShouldBe(("127.0.0.1", 31001, "user-1", "pass-1"));

        SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], fake, ConnectionSettings.Default, ["/opt/game/game", "/x/xray"],
            ["/opt/game/game"]))["route"]!["rules"]!.AsArray().ShouldHaveSingleItem("нет входа обхода — нет и правила");
    }

    [Theory]
    [InlineData("INFO [3870541312 0ms] inbound/tun[tun-in]: inbound connection to 203.0.113.24:443", CoreLogLevel.Warning, false)]
    [InlineData("INFO [3870541312 0ms] inbound/tun[tun-in]: inbound connection to 203.0.113.24:443", CoreLogLevel.Info, true)]
    [InlineData("DEBUG [1 0ms] router: match[0]", CoreLogLevel.Info, false)]
    [InlineData("DEBUG [1 0ms] router: match[0]", CoreLogLevel.Debug, true)]
    [InlineData("INFO sing-box started (0.31s)", CoreLogLevel.Warning, false)]
    [InlineData("WARN inbound/tun[tun-in]: open interface take too much time", CoreLogLevel.Warning, true)]
    [InlineData("ERROR [1 10ms] connection: open outbound connection: dial tcp: refused", CoreLogLevel.Warning, true)]
    [InlineData("FATAL start service: start inbound/tun[tun-in]: configure tun interface: operation not permitted", CoreLogLevel.Warning, true)]
    [InlineData("goroutine 1 [running]:", CoreLogLevel.Warning, true)]
    public void Sing_box_lines_below_the_core_log_level_stay_out_of_the_journal(string line, CoreLogLevel level, bool shown)
    {
        SingBoxConfigBuilder.ShownLine(line, level).ShouldBe(shown);
    }

    private static JsonObject Build(IReadOnlyList<IPAddress> servers, ProtectionSettings? protection = null, ConnectionSettings? connection = null) =>
        SingBoxConfigBuilder.BuildTun(new TunRequest(Link, servers, protection ?? ProtectionSettings.Default, connection ?? ConnectionSettings.Default));

    private static JsonObject Tun(JsonObject config) => config["inbounds"]!.AsArray().ShouldHaveSingleItem()!.AsObject();

    private static List<string> Strings(JsonNode? array) => [.. array!.AsArray().Select(n => n!.GetValue<string>())];
}
