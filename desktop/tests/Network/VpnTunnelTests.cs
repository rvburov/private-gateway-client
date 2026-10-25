using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Туннель режима VPN: клиент в своем сетевом пространстве Linux → TUN sing-box → Xray → свой сервер
/// Xray → сайт и DNS теста. Оба ядра работают в пространстве, как на машине; сервер — на адресе машины
/// по паре veth. Сеть машины не трогается: TUN и его маршруты — только в пространстве теста. Тестам с
/// TUN нужны root, unshare, nsenter, curl и python3, Xray и sing-box.
/// </summary>
[Collection(NamespaceSerial.Name)]
public sealed class VpnTunnelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Traffic_of_the_namespace_goes_through_tun_and_the_server()
    {
        SkipWithoutNamespaces();
        using var netns = NetworkNamespace.Create();
        var (host, neighbor, _) = netns.LinkAsMachine();
        var rulesBefore = await netns.RunAsync("ip", "rule", "show");
        using var site = new LocalSite("через туннель");
        using var lan = new LocalSite("локальная сеть", neighbor);
        using var dns = new TestDns(IPAddress.Parse("1.1.1.1"));
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host);
        // Выход сервера теста — на свой сайт и свой DNS: из машины ничего не уходит.
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dns.Port}" } });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" }),
        };

        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            // DNS Xray — обычный, по UDP через сервер: на сервере теста его отвечает свой DNS. Строгие маршруты — только если в
            // ядре есть IPv6 (в контейнере облака его нет): служба смотрит сама.
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" }, ConnectionSettings.Default, RoutingSettings.Default);

        var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], TimeSpan.FromSeconds(20),
            TimeProvider.System, netns.Path, cancellationToken: Ct);
        try
        {
            (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "публичный адрес — через TUN, Xray и сервер");
            site.Requests.ShouldBe(1);
            (await netns.ResolveAsync("example.com", IPAddress.Parse("192.0.2.53"))).ShouldBe("1.1.1.1",
                "DNS к любому адресу отвечает Xray — через сервер, а не DNS провайдера или роутера");
            dns.Queries.ShouldContain("example.com");
            await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(lan.Url),
                "локальная сеть не разрешена — закрыта и в режиме VPN");
            lan.Requests.ShouldBe(0);
        }
        finally
        {
            await tunnel.DisposeAsync();
        }

        await Should.ThrowAsync<InvalidOperationException>(() => netns.CurlAsync(new Uri("http://1.1.1.1/"), seconds: 3),
            "туннеля нет — и пути из пространства нет");
        site.Requests.ShouldBe(1);
        (await netns.RunAsync("ip", "rule", "show")).ShouldBe(rulesBefore, "sing-box ушел сам и убрал свои правила маршрутизации");
    }

    [Fact]
    public async Task Fake_dns_answers_from_its_pool_and_the_server_gets_the_name()
    {
        SkipWithoutNamespaces();
        using var netns = NetworkNamespace.Create();
        var (host, _, _) = netns.LinkAsMachine();
        using var site = new LocalSite("через туннель по имени");
        using var dns = new TestDns(IPAddress.Parse("1.1.1.1"));
        var (definition, server) = await NameOnlyServerAsync(host, site, dns);
        using var _ = server;
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true, Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default, RoutingSettings.Default);

        await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], TimeSpan.FromSeconds(20),
            TimeProvider.System, netns.Path, cancellationToken: Ct);

        var fake = IPAddress.Parse(await netns.ResolveAsync("example.com", IPAddress.Parse("192.0.2.53")));
        IPNetwork.Parse(XrayConfigBuilder.FakeDnsPool).Contains(fake).ShouldBeTrue($"{fake} — адрес из пула FakeDNS");
        dns.Queries.ShouldNotContain("example.com", "адрес из пула — сразу, без запроса к DNS");
        (await netns.RunAsync("curl", "-sS", "-m", "10", "--resolve", $"example.com:80:{fake}", "http://example.com/"))
            .ShouldBe("через туннель по имени", "Xray узнал имя по адресу из пула и отдал серверу имя, а не адрес");
        site.Requests.ShouldBe(1);
        dns.Queries.ShouldNotContain("example.com", "имя разрешает сервер: правилам адрес не нужен — локальная сеть закрыта, правил для адресов нет");
    }

    [Fact]
    public async Task Fake_dns_address_of_a_program_around_the_tunnel_is_dialed_directly_by_name()
    {
        SkipWithoutNamespaces();
        using var netns = NetworkNamespace.Create();
        var (host, neighbor, _) = netns.LinkAsMachine();
        using var site = new LocalSite("через туннель по имени");
        using var lan = new LocalSite("мимо туннеля", neighbor);
        // Настоящий адрес имени — у сайта мимо туннеля.
        using var dns = new TestDns(neighbor);
        var (definition, server) = await NameOnlyServerAsync(host, site, dns);
        using var _ = server;
        var curl = VpnTunnel.RealPath((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':')
            .Select(dir => Path.Combine(dir, "curl")).First(File.Exists));
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { FakeDns = true, Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default,
            new RoutingSettings(RoutingMode.Split, [new BypassApp(curl, "curl")], []));

        await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], TimeSpan.FromSeconds(20),
            TimeProvider.System, netns.Path, cancellationToken: Ct);

        // DNS за программу спросила другая (как служба DNS ОС) — через туннель, и ответ — из пула.
        var fake = await netns.ResolveAsync("example.com", IPAddress.Parse("192.0.2.53"));
        IPNetwork.Parse(XrayConfigBuilder.FakeDnsPool).Contains(IPAddress.Parse(fake)).ShouldBeTrue();
        var port = lan.Url.Port;
        (await netns.RunAsync("curl", "-sS", "-m", "10", "--resolve", $"example.com:{port}:{fake}", $"http://example.com:{port}/"))
            .ShouldBe("мимо туннеля", "программа мимо туннеля пришла на адрес из пула — Xray соединил ее напрямую с настоящим адресом имени");
        lan.Requests.ShouldBe(1);
        (await netns.FetchAsync(new Uri($"http://{fake}:{port}/"))).ShouldBe("через туннель по имени", "другая программа — через туннель");
    }

    [Theory]
    [InlineData("2026/10/25 14:40:32.311055 [Warning] core: Xray 25.8.3 started", true)]
    [InlineData("2026/10/25 14:40:32.278469 [Info] infra/conf/serial: Reading config: &{Name:stdin: Format:json}", false)]
    [InlineData("Xray 25.8.3 (Xray, Penetrates Everything.) Custom (go1.24.7 linux/amd64)", false)]
    [InlineData("[Info] app/dispatcher: taking detour [proxy] for [tcp:started]", false)]
    public void Xray_is_ready_by_its_started_line(string line, bool started)
    {
        VpnTunnel.XrayStarted(line).ShouldBe(started);
    }

    [Fact]
    public async Task Xray_that_dies_at_start_gives_no_tunnel()
    {
        SkipWithoutNamespaces();
        using var netns = NetworkNamespace.Create();
        var (definition, _) = XrayKit.Pair("vless", XrayKit.FreePort());
        var request = new CoreRequest(definition, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default with { StrictRoutes = false },
            RoutingSettings.Default);

        // Вместо Xray — sing-box: аргументов Xray он не знает и сразу выходит.
        var error = await Should.ThrowAsync<TunnelException>(() =>
            VpnTunnel.StartAsync(XrayKit.SingBox!, XrayKit.SingBox!, request, [IPAddress.Loopback], TimeSpan.FromSeconds(20), TimeProvider.System, netns.Path,
                cancellationToken: Ct));

        error.Message.ShouldStartWith("Xray вышел при запуске", Case.Sensitive, "TUN без Xray не поднимается");
    }

    [Fact]
    public void Each_connection_gets_its_own_port_and_password()
    {
        var first = VpnTunnel.NewLink(IPAddress.Parse("198.51.100.20"));
        var second = VpnTunnel.NewLink(IPAddress.Parse("198.51.100.20"));

        first.ServerAddress.ShouldBe("198.51.100.20");
        new[] { first.User, first.Password, second.User, second.Password }.ShouldAllBe(s => s.Length == 32 && s.All(Uri.IsHexDigit),
            "128 случайных бит — пароль не угадать");
        new[] { first.User, first.Password, second.User, second.Password }.Distinct().Count().ShouldBe(4);
        first.Port.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Server_the_core_refuses_does_not_start_a_tunnel()
    {
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var hysteria = XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password)) with
        {
            Transport = TransportOptions.Quic,
        };
        var request = new CoreRequest(hysteria, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);

        var error = await Should.ThrowAsync<TunnelException>(() =>
            VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [IPAddress.Parse("192.0.2.10")], TimeSpan.FromSeconds(5), TimeProvider.System,
                cancellationToken: Ct));

        error.Message.ShouldContain("не возьмется");
    }

    [Fact]
    public async Task Stop_lets_sing_box_leave_by_itself_before_killing_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM — на macOS и Linux");
        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        // Без TUN — только вход на 127.0.0.1: прав не нужно, сеть машины не меняется.
        var config = JsonNode.Parse($$"""
            {
              "log": { "level": "warn" },
              "inbounds": [{ "type": "mixed", "listen": "127.0.0.1", "listen_port": {{XrayKit.FreePort()}} }],
              "outbounds": [{ "type": "direct" }]
            }
            """)!.AsObject();

        using var asked = CoreProcess.StartSingBox(XrayKit.SingBox!, config);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await asked.StopAsync(VpnTunnel.StopGrace, Ct);
        asked.ExitCode.ShouldBe(0, "sing-box ушел сам — успел убрать маршруты");

        using var killed = CoreProcess.StartSingBox(XrayKit.SingBox!, config);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        await killed.StopAsync(cancellationToken: Ct);
        killed.ExitCode.ShouldNotBe(0, "без отсрочки ядро снимается сразу");
    }

    [Fact]
    public void Real_path_resolves_links_in_every_part()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("символические ссылки в путях программ — на Linux и macOS");
            return;
        }

        var root = Directory.CreateTempSubdirectory("pg-real-").FullName;
        try
        {
            // Цель ссылки сама идет через ссылку на папку — как /var → /private/var на macOS.
            var top = Directory.CreateDirectory(Path.Combine(root, "top")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), top);
            var real = Directory.CreateDirectory(Path.Combine(root, "alias", "lib")).FullName;
            File.WriteAllText(Path.Combine(real, "app"), string.Empty);
            Directory.CreateSymbolicLink(Path.Combine(root, "bin"), real);
            File.CreateSymbolicLink(Path.Combine(real, "app-link"), "app");
            File.CreateSymbolicLink(Path.Combine(root, "loop"), Path.Combine(root, "loop"));
            var expected = Path.Combine(VpnTunnel.RealPath(top), "lib", "app");

            VpnTunnel.RealPath(Path.Combine(root, "bin", "app-link")).ShouldBe(expected, "ссылка на папку и ссылка на файл — как путь процесса у ОС");
            VpnTunnel.RealPath(Path.Combine(root, "bin", "..", "bin", "app")).ShouldBe(expected);
            VpnTunnel.RealPath(Path.Combine(root, "bin", "gone")).ShouldBe(Path.Combine(VpnTunnel.RealPath(top), "lib", "gone"), "удаленная программа — путь как есть");
            Should.NotThrow(() => VpnTunnel.RealPath(Path.Combine(root, "loop")), "ссылка сама на себя не уводит в бесконечность");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Сервер теста на адресе машины: к сайту пускает только по имени example.com, соединение с адресом — в блок;
    /// DNS (порт 53) — на DNS теста.
    /// </summary>
    private static async Task<(ServerDefinition Definition, CoreProcess Server)> NameOnlyServerAsync(IPAddress host, LocalSite site, TestDns dns)
    {
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dns.Port}" } },
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });
        serverConfig["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" },
                new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("full:example.com"), ["outboundTag"] = "site" },
                new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("0.0.0.0/0", "::/0"), ["outboundTag"] = "block" }),
        };
        return (definition, await XrayKit.Start(serverConfig, serverPort));
    }

    [Fact]
    public async Task Adapter_the_os_gives_no_ipv6_gets_a_tunnel_without_ipv6()
    {
        SkipWithoutNamespaces();
        NetworkNamespace.SkipUnlessIpv6();
        using var netns = NetworkNamespace.Create();
        var (host, _, _) = netns.LinkAsMachine();
        using var site = new LocalSite("через туннель");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        // Новые адаптеры в пространстве — без IPv6, как Windows с выключенным IPv6: адрес IPv6 sing-box не поставит и выйдет.
        netns.Run(null, "sysctl", "-qw", "net.ipv6.conf.default.disable_ipv6=1");
        var lines = new System.Collections.Concurrent.ConcurrentQueue<(string Source, string Line)>();
        var request = new CoreRequest(definition, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);

        await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], TimeSpan.FromSeconds(20),
            TimeProvider.System, netns.Path, (source, line) => lines.Enqueue((source, line)), VpnTunnel.NewProbe(),
            (_, _) => Task.FromResult(true), Ct);

        tunnel.Ipv6.ShouldBeFalse("сервер открывает IPv6, но адаптеру ОС его не дала");
        lines.ShouldContain(("service", "sing-box не поднял адаптер с IPv6 — на это подключение IPv6 закрыт"));
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "туннель поднят без IPv6");
    }

    [Fact]
    public async Task Server_over_ipv6_carries_ipv6_through_the_tunnel_without_a_check()
    {
        SkipWithoutNamespaces();
        NetworkNamespace.SkipUnlessIpv6();
        using var netns = NetworkNamespace.Create();
        netns.LinkAsMachine();
        var (host6, _) = netns.LinkIpv6();
        using var site = new LocalSite("через туннель");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host6);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
        var asked = 0;

        await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host6], TimeSpan.FromSeconds(20),
            TimeProvider.System, netns.Path, probe: VpnTunnel.NewProbe(), serverIpv6: (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult(false);
            }, cancellationToken: Ct);

        (tunnel.Ipv6, asked).ShouldBe((true, 0), "с сервером Xray соединяется по IPv6 — IPv6 у него есть, без проверки");
        (await netns.CurlAsync(new Uri("http://[2606:4700:4700::1111]/"))).ShouldBe("через туннель", "IPv6 — через TUN и сервер по IPv6");
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель", "и IPv4");
    }

    [Fact]
    public async Task Slow_server_ipv6_check_does_not_eat_the_connect_time()
    {
        SkipWithoutNamespaces();
        NetworkNamespace.SkipUnlessIpv6();
        using var netns = NetworkNamespace.Create();
        var (host, _, _) = netns.LinkAsMachine();
        using var site = new LocalSite("через туннель");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort, host);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<(string Source, string Line)>();

        // Сервер молчит по IPv6 дольше всего времени подключения: проверка берет не больше половины, остальное — sing-box.
        await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, XrayKit.SingBox!, request, [host], TimeSpan.FromSeconds(12),
            TimeProvider.System, netns.Path, (source, line) => lines.Enqueue((source, line)), VpnTunnel.NewProbe(), async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return true;
            }, Ct);

        tunnel.Ipv6.ShouldBeFalse();
        lines.ShouldContain(("service", "Проверка IPv6 сервера не уложилась во время подключения — на это подключение IPv6 закрыт"));
        (await netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
    }

    [Fact]
    public void Machine_ipv6_is_a_global_address_and_not_just_a_link_local_one()
    {
        SkipWithoutNamespaces();
        NetworkNamespace.SkipUnlessIpv6();
        using var netns = NetworkNamespace.Create();
        netns.LinkAsMachine();

        VpnTunnel.MachineIpv6(netns.Path).ShouldBeFalse("только адрес fe80:: — IPv6 мимо туннеля уйти некуда");
        netns.LinkIpv6();
        VpnTunnel.MachineIpv6(netns.Path).ShouldBeTrue();
    }

    private static void SkipWithoutNamespaces()
    {
        NetworkNamespace.SkipUnlessAvailable("curl", "python3", "ip");
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
    }
}
