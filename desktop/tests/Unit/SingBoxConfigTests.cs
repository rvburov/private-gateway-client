using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Конфиг sing-box для TUN режима VPN: весь трафик и DNS — в Xray, сервер — мимо туннеля.</summary>
public sealed class SingBoxConfigTests
{
    /// <summary>Ключ связи с Xray — заведомо ненастоящий: 16 байт 0x11 в base64.</summary>
    private static readonly TunLink Link = new(31000, "EREREREREREREREREREREQ==", "198.51.100.20");

    /// <summary>IPv6 есть в ядре, через туннель не идет — сервер его не открывает.</summary>
    private static readonly TunIpv6 Closed = new(Kernel: true, Tunnel: false);

    [Fact]
    public void Everything_goes_through_xray_and_the_server_goes_around_the_tunnel()
    {
        var config = Build([IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20")]);

        var tun = Tun(config);
        (tun["auto_route"]!.GetValue<bool>(), tun["strict_route"]!.GetValue<bool>(), tun["stack"]!.GetValue<string>())
            .ShouldBe((true, true, "system"));
        Strings(tun["address"]).ShouldBe([SingBoxConfigBuilder.TunAddress], "сервер не открывает IPv6 — у TUN его нет");
        Strings(tun["route_exclude_address"]).ShouldBe(["198.51.100.20/32", "2001:db8::20/128"], "локальная сеть не разрешена — мимо туннеля только сервер");
        tun.ContainsKey("mtu").ShouldBeFalse("MTU подбирает sing-box");
        tun["interface_name"]!.GetValue<string>().ShouldBe(OperatingSystem.IsWindows() ? "Private Gateway" : OperatingSystem.IsMacOS() ? "utun225" : "pgtun0",
            "по имени адаптера туннель узнают правила блока при обрыве; на macOS — только utunN, номер свой");

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
        (xray["type"]!.GetValue<string>(), xray["server"]!.GetValue<string>(), xray["server_port"]!.GetValue<int>()).ShouldBe(("shadowsocks", "127.0.0.1", 31000));
        (xray["method"]!.GetValue<string>(), xray["password"]!.GetValue<string>()).ShouldBe((TunLink.Method, Link.Key), "связь с Xray — Shadowsocks 2022 (решение 481)");
        xray.AsObject().ContainsKey("udp_over_tcp").ShouldBeFalse("UDP — датаграммами");
        var route = config["route"]!.AsObject();
        route["final"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.XrayTag);
        route["auto_detect_interface"]!.GetValue<bool>().ShouldBeTrue();
        var dns = route["rules"]!.AsArray().ShouldHaveSingleItem("и DNS — в Xray: его отвечает выход DNS Xray, а не sing-box и не провайдер")!;
        (string.Join(',', Strings(dns["ip_cidr"])), dns["port"]!.GetValue<int>(), dns["outbound"]!.GetValue<string>())
            .ShouldBe(("172.19.0.2/32", 53, SingBoxConfigBuilder.XrayTag), "DNS к адресу туннеля — в Xray от любой программы, и мимо туннеля тоже");
        config.ContainsKey("dns").ShouldBeFalse("своего DNS у sing-box нет — имен он не ищет");
        config["log"]!["level"]!.GetValue<string>().ShouldBe("info", "строка «sing-box started» — признак готовности; адреса соединений в журнал не пускает служба");
    }

    [Fact]
    public void Own_dns_in_a_private_network_goes_around_the_tunnel()
    {
        var tun = Tun(Build([IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "192.168.1.53" }));

        Strings(tun["route_exclude_address"]).ShouldBe(["198.51.100.20/32", "192.168.1.53/32"], "Xray спрашивает свой DNS напрямую, локальная сеть закрыта");
    }

    [Fact]
    public void Settings_shape_the_tunnel()
    {
        var config = Build([IPAddress.Parse("198.51.100.20")],
            ProtectionSettings.Default with { AllowLan = true },
            ConnectionSettings.Default with { Stack = TunEngine.Gvisor, Mtu = 1420, StrictRoutes = false, CoreLog = CoreLogLevel.Debug },
            Closed with { Tunnel = true });

        var tun = Tun(config);
        Strings(tun["address"]).ShouldBe([SingBoxConfigBuilder.TunAddress, SingBoxConfigBuilder.TunAddress6]);
        Strings(tun["route_exclude_address"]).ShouldContain("192.168.0.0/16", "локальная сеть разрешена — мимо туннеля");
        Strings(tun["route_exclude_address"]).ShouldContain("10.0.0.0/8");
        (tun["stack"]!.GetValue<string>(), tun["mtu"]!.GetValue<int>(), tun["strict_route"]!.GetValue<bool>()).ShouldBe(("gvisor", 1420, false));
        config["log"]!["level"]!.GetValue<string>().ShouldBe("debug");

        Tun(Build([IPAddress.Parse("198.51.100.20")], connection: ConnectionSettings.Default with { Stack = TunEngine.Mixed }))["stack"]!
            .GetValue<string>().ShouldBe("mixed");
        Strings(Tun(Build([IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default with { Addresses = AddressPreference.Ipv4Only },
                ipv6: Closed with { Tunnel = true }))["address"])
            .ShouldBe([SingBoxConfigBuilder.TunAddress], "только IPv4 — и у TUN только IPv4, даже если сервер открывает IPv6");
    }

    [Theory]
    [InlineData(true, AddressPreference.PreferIpv4, false, true, "IPv6 вне туннеля блокируется — его закрывают строгие маршруты")]
    [InlineData(false, AddressPreference.PreferIpv4, false, false, "IPv6 вне туннеля разрешен — идет напрямую")]
    [InlineData(false, AddressPreference.Ipv4Only, false, true, "только IPv4 — IPv6 закрыт везде")]
    [InlineData(false, AddressPreference.Ipv4Only, true, true, "только IPv4 — и сервер с IPv6 его не открывает")]
    [InlineData(true, AddressPreference.PreferIpv4, true, false, "IPv6 идет через туннель — закрывать нечего")]
    public void Ipv6_outside_the_tunnel_is_closed_by_strict_routes_even_when_they_are_off(
        bool blockIpv6, AddressPreference addresses, bool tunnel, bool strict, string because)
    {
        var tun = Tun(Build([IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default with { BlockIpv6 = blockIpv6, Addresses = addresses },
            ConnectionSettings.Default with { StrictRoutes = false }, Closed with { Tunnel = tunnel }));

        tun["strict_route"]!.GetValue<bool>().ShouldBe(strict, because);
        SingBoxConfigBuilder.Ipv6Outside(new TunRequest(Link, [], ProtectionSettings.Default with { BlockIpv6 = blockIpv6, Addresses = addresses },
            ConnectionSettings.Default with { StrictRoutes = false }, Closed with { Tunnel = tunnel })).ShouldBe(!strict && !tunnel);
    }

    [Fact]
    public void Server_over_ipv6_keeps_its_connection_when_ipv6_is_not_tunneled()
    {
        var link = Link with { ServerAddress = "2001:db8::20" };
        TunRequest Request(bool tunnel) => new(link, [IPAddress.Parse("2001:db8::20")], ProtectionSettings.Default, ConnectionSettings.Default,
            Closed with { Tunnel = tunnel });

        Tun(SingBoxConfigBuilder.BuildTun(Request(tunnel: false)))["strict_route"]!.GetValue<bool>()
            .ShouldBeFalse("строгие маршруты без IPv6 у TUN закрыли бы и соединение Xray с сервером по IPv6");
        SingBoxConfigBuilder.Ipv6Outside(Request(tunnel: false)).ShouldBeTrue("IPv6 программ тогда держит только блок при обрыве — это видно");
        Tun(SingBoxConfigBuilder.BuildTun(Request(tunnel: true)))["strict_route"]!.GetValue<bool>().ShouldBeTrue("IPv6 через туннель — сервер мимо него");
    }

    [Fact]
    public void Linux_tunnel_routes_are_halves_so_no_narrower_route_goes_around_it()
    {
        var closed = Tun(Build([IPAddress.Parse("198.51.100.20")]));
        var tunneled = Tun(Build([IPAddress.Parse("198.51.100.20")], ipv6: Closed with { Tunnel = true }));

        if (OperatingSystem.IsLinux())
        {
            Strings(closed["route_address"]).ShouldBe(["0.0.0.0/1", "128.0.0.0/1"]);
            Strings(tunneled["route_address"]).ShouldBe(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"],
                "маршрут ::/0 правило sing-box пропускает, и адрес с более узким маршрутом в основной таблице ушел бы мимо туннеля");
        }
        else
        {
            closed.ContainsKey("route_address").ShouldBeFalse("правила по таблицам — только в Linux");
        }
    }

    [Fact]
    public void Linux_without_ipv6_in_the_kernel_gets_no_strict_routes()
    {
        var tun = Tun(Build([IPAddress.Parse("198.51.100.20")], ipv6: new TunIpv6(Kernel: false, Tunnel: false)));

        tun["strict_route"]!.GetValue<bool>().ShouldBe(!OperatingSystem.IsLinux(),
            "на Linux без IPv6 в ядре sing-box со строгими маршрутами не стартует, а закрывать там нечего");
        SingBoxConfigBuilder.Ipv6Outside(new TunRequest(Link, [], ProtectionSettings.Default, ConnectionSettings.Default, new TunIpv6(Kernel: false, Tunnel: false)))
            .ShouldBeFalse("IPv6 в ОС нет — и мимо туннеля ему идти некуда");
        Strings(tun["address"]).ShouldBe([SingBoxConfigBuilder.TunAddress]);
    }

    [Fact]
    public void Chosen_programs_go_around_the_tunnel_through_a_marked_exit()
    {
        var config = SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default,
            ConnectionSettings.Default, Closed, ["/opt/game/game", "/usr/bin/xray"]));

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

        var rules = config["route"]!["rules"]!.AsArray();
        rules[0]!["port"]!.GetValue<int>().ShouldBe(53, "DNS к адресу туннеля — первым: напрямую через карту на него ответа нет");
        var rule = rules.Skip(1).ShouldHaveSingleItem()!;
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
        Build([IPAddress.Parse("198.51.100.20")])["route"]!["rules"]!.AsArray().Count.ShouldBe(1, "только DNS к адресу туннеля");
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
            ConnectionSettings.Default, Closed, ["/opt/game/game", "/x/xray"], ["/opt/game/game"]));

        var rules = config["route"]!["rules"]!.AsArray();
        rules.Count.ShouldBe(3);
        (string.Join(',', Strings(rules[1]!["process_path"])), string.Join(',', Strings(rules[1]!["ip_cidr"])), rules[1]!["outbound"]!.GetValue<string>())
            .ShouldBe(("/opt/game/game", XrayConfigBuilder.FakeDnsPool, SingBoxConfigBuilder.BypassTag),
                "адрес FakeDNS у программы мимо туннеля — во вход обхода Xray, раньше правила «напрямую»");
        rules[2]!["outbound"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.DirectTag);
        var bypass = config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == SingBoxConfigBuilder.BypassTag)!;
        (bypass["type"]!.GetValue<string>(), bypass["server"]!.GetValue<string>(), bypass["server_port"]!.GetValue<int>(), bypass["password"]!.GetValue<string>())
            .ShouldBe(("shadowsocks", "127.0.0.1", 31001, Link.Key));

        SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], fake, ConnectionSettings.Default, Closed, ["/opt/game/game", "/x/xray"],
            ["/opt/game/game"]))["route"]!["rules"]!.AsArray().Count.ShouldBe(2, "нет входа обхода — нет и правила");
    }

    [Fact]
    public void Narrow_routes_of_a_ci_test_take_only_their_networks_and_no_strict_routes()
    {
        var tun = Tun(SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default,
            ConnectionSettings.Default with { StrictRoutes = true }, Closed, Routes: ["1.1.1.1/32", "198.18.0.0/15"])));

        Strings(tun["route_address"]).ShouldBe(["1.1.1.1/32", "198.18.0.0/15"]);
        tun["strict_route"]!.GetValue<bool>().ShouldBeFalse("строгие маршруты закрыли бы машине CI все, что мимо узкого маршрута");
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

    [Theory]
    [InlineData("", "172.19.0.1")]
    [InlineData("192.168.1.10/24 10.0.0.5/8", "172.19.0.1")]
    [InlineData("172.19.0.1/20", "10.255.253.1")]
    [InlineData("172.19.0.2/30", "10.255.253.1")]
    [InlineData("172.19.3.1/16 10.255.0.1/16", "192.168.253.253")]
    [InlineData("172.16.0.1/12 10.0.0.1/8 192.168.0.1/16", "172.19.0.1")]
    [InlineData("172.16.0.1/12 10.0.0.1/8 192.168.0.1/17", "192.168.253.253")]
    [InlineData("0.0.0.0/0 172.16.0.0/7 172.19.0.1/20", "10.255.253.1")]
    [InlineData("ac13:1::1/32 2001:db8::1/64", "172.19.0.1")]
    public void Tun_takes_the_first_subnet_no_adapter_of_the_machine_holds(string taken, string address)
    {
        var networks = taken.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => (IPAddress.Parse(n.Split('/')[0]), int.Parse(n.Split('/')[1], System.Globalization.CultureInfo.InvariantCulture)));

        TunSubnet.Choose(networks).Address.ShouldBe(IPAddress.Parse(address),
            "172.19.0.1 занят (Hyper-V, WSL, Docker, TUN другого клиента) — следующая свободная; сети короче /8 и IPv6 не в счет; заняты все — прежняя");
    }

    [Fact]
    public void Default_subnet_is_the_former_constant_and_every_candidate_is_its_own_slash_30()
    {
        (TunSubnet.Default.Cidr, TunSubnet.Default.Dns.ToString()).ShouldBe((SingBoxConfigBuilder.TunAddress, SingBoxConfigBuilder.TunDns));
        TunSubnet.Candidates.Select(c => (c.Cidr, c.Dns.ToString())).ShouldBe(
        [
            ("172.19.0.1/30", "172.19.0.2"), ("10.255.253.1/30", "10.255.253.2"), ("192.168.253.253/30", "192.168.253.254"),
            ("172.29.253.253/30", "172.29.253.254"), ("10.199.199.1/30", "10.199.199.2"),
        ]);
        foreach (var candidate in TunSubnet.Candidates)
        {
            TunSubnet.Choose(TunSubnet.Candidates.Where(c => c != candidate).Select(c => (c.Address, 30))).ShouldBe(candidate,
                "подсети кандидатов не пересекаются друг с другом");
        }
    }

    [Fact]
    public void Tun_on_another_subnet_gives_the_os_and_xray_its_own_dns_address()
    {
        var subnet = TunSubnet.Candidates[1];
        var config = SingBoxConfigBuilder.BuildTun(new TunRequest(Link, [IPAddress.Parse("198.51.100.20")], ProtectionSettings.Default,
            ConnectionSettings.Default, Closed, Subnet: subnet));

        var tun = Tun(config);
        Strings(tun["address"]).ShouldBe(["10.255.253.1/30"]);
        Strings(tun["dns_address"]).ShouldBe(["10.255.253.2"], "этот адрес sing-box дает DNS ОС");
        Strings(config["route"]!["rules"]!.AsArray().ShouldHaveSingleItem()!["ip_cidr"]).ShouldBe(["10.255.253.2/32"],
            "DNS к адресу туннеля — в Xray, а не к прежнему 172.19.0.2");
    }

    private static JsonObject Build(
        IReadOnlyList<IPAddress> servers, ProtectionSettings? protection = null, ConnectionSettings? connection = null, TunIpv6? ipv6 = null) =>
        SingBoxConfigBuilder.BuildTun(new TunRequest(Link, servers, protection ?? ProtectionSettings.Default, connection ?? ConnectionSettings.Default,
            ipv6 ?? Closed));

    private static JsonObject Tun(JsonObject config) => config["inbounds"]!.AsArray().ShouldHaveSingleItem()!.AsObject();

    private static List<string> Strings(JsonNode? array) => [.. array!.AsArray().Select(n => n!.GetValue<string>())];
}
