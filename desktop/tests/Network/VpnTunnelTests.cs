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
            // DNS Xray — обычный, по UDP через сервер: на сервере теста его отвечает свой DNS.
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            // Строгим маршрутам нужен IPv6 в ядре; в контейнере облака его нет.
            ConnectionSettings.Default with { StrictRoutes = Directory.Exists("/proc/sys/net/ipv6") }, RoutingSettings.Default);

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
            var real = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
            File.WriteAllText(Path.Combine(real, "app"), string.Empty);
            Directory.CreateSymbolicLink(Path.Combine(root, "bin"), real);
            File.CreateSymbolicLink(Path.Combine(real, "app-link"), "app");
            var expected = Path.Combine(VpnTunnel.RealPath(real), "app");

            VpnTunnel.RealPath(Path.Combine(root, "bin", "app-link")).ShouldBe(expected, "ссылка на папку и ссылка на файл — как путь процесса у ОС");
            VpnTunnel.RealPath(Path.Combine(root, "bin", "..", "bin", "app")).ShouldBe(expected);
            VpnTunnel.RealPath(Path.Combine(root, "bin", "gone")).ShouldBe(Path.Combine(VpnTunnel.RealPath(real), "gone"), "удаленная программа — путь как есть");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SkipWithoutNamespaces()
    {
        NetworkNamespace.SkipUnlessAvailable("curl", "python3", "ip");
        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
    }
}
