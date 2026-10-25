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

        var xray = config["outbounds"]!.AsArray().ShouldHaveSingleItem()!;
        (xray["type"]!.GetValue<string>(), xray["server"]!.GetValue<string>(), xray["server_port"]!.GetValue<int>()).ShouldBe(("socks", "127.0.0.1", 31000));
        (xray["username"]!.GetValue<string>(), xray["password"]!.GetValue<string>()).ShouldBe(("user-1", "pass-1"));
        var route = config["route"]!.AsObject();
        route["final"]!.GetValue<string>().ShouldBe(SingBoxConfigBuilder.XrayTag);
        route["auto_detect_interface"]!.GetValue<bool>().ShouldBeTrue();
        route.ContainsKey("rules").ShouldBeFalse("и DNS — в Xray: его отвечает выход DNS Xray, а не sing-box и не провайдер");
        config.ContainsKey("dns").ShouldBeFalse("своего DNS у sing-box нет — имен он не ищет");
        config["log"]!["level"]!.GetValue<string>().ShouldBe("warn", "журнал соединений с адресами сайтов не пишется");
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

    private static JsonObject Build(IReadOnlyList<IPAddress> servers, ProtectionSettings? protection = null, ConnectionSettings? connection = null) =>
        SingBoxConfigBuilder.BuildTun(new TunRequest(Link, servers, protection ?? ProtectionSettings.Default, connection ?? ConnectionSettings.Default));

    private static JsonObject Tun(JsonObject config) => config["inbounds"]!.AsArray().ShouldHaveSingleItem()!.AsObject();

    private static List<string> Strings(JsonNode? array) => [.. array!.AsArray().Select(n => n!.GetValue<string>())];
}
