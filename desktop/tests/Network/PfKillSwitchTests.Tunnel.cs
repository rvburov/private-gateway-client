using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class PfKillSwitchTests
{
    /// <summary>
    /// Настоящий TUN и pf: сайт и DNS отвечают за двумя Xray на петле. Маршруты только к адресу теста и DNS TUN,
    /// системный DNS не меняется. Родительский якорь применяет правила только к тестовому TUN: остальная сеть CI открыта.
    /// Проверка без pf, с правилами программы и с запретом TUN отличает отказ туннеля от отказа его фильтрации.
    /// </summary>
    [Theory]
    [InlineData(TunEngine.System)]
    [InlineData(TunEngine.Gvisor)]
    public async Task Real_tun_carries_http_and_dns_with_pf(TunEngine stack)
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("pf — только на macOS от root (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var name = SingBoxConfigBuilder.InterfaceName;
        NetworkInterface.GetAllNetworkInterfaces().Any(n => n.Name == name).ShouldBeFalse("чужой или уже работающий TUN не трогаем");
        var cancellation = TestContext.Current.CancellationToken;
        var address = IPAddress.Parse("198.51.100.25");
        using var site = new LocalSite("HTTP через TUN macOS под pf");
        using var dns = new TestDns(address);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        EndpointTunnelFixture.RouteSite(serverConfig, address, site.Url.Port, dns.Port);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8", FakeDns = false },
            ConnectionSettings.Default with { Stack = stack, CoreLog = CoreLogLevel.Debug }, RoutingSettings.Default);
        var output = new CoreOutput();
        var link = VpnTunnel.NewLink(IPAddress.Loopback);
        var config = XrayConfigBuilder.Build(request, link).Config.ShouldNotBeNull();
        EndpointTunnelFixture.AllowTestAddress(config, address);
        using var xray = CoreProcess.StartXray(XrayKit.Xray!, config, output: line => output.Add("xray", line));
        await WaitForXray();
        var subnet = TunSubnet.Choose(VpnTunnel.MachineNetworks());
        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(link, [IPAddress.Loopback], serverPort, request.Protection, request.Connection,
            new TunIpv6(Kernel: true, Tunnel: false), Routes: [address + "/32", subnet.Dns + "/32"], Subnet: subnet));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, output: line =>
        {
            output.Add("sing-box", line);
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                started.TrySetResult();
            }
        });
        var parent = "com.apple/249.pg-test-" + Guid.NewGuid().ToString("N")[..8];
        var pf = new PfKillSwitch(Pfctl, Path.Combine(_root, "tun-pf.token"), parent + "/traffic");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation);
            (await output.TraceAsync(Page, "HTTP до pf")).ShouldBe("HTTP через TUN macOS под pf");
            await output.TraceAsync(Dns, "DNS до pf");
            var scope = Pfctl(["-a", parent, "-f", "-"], $"anchor \"traffic\" on {name} all\n");
            scope.Code.ShouldBe(0, scope.Error);
            var policy = new KillSwitchPolicy(name, [new(IPAddress.Loopback, serverPort)]);
            pf.Enable(policy);
            (await output.TraceAsync(Page, "HTTP после pf")).ShouldBe("HTTP через TUN macOS под pf");
            await output.TraceAsync(Dns, "DNS после pf");

            // Отсутствующий разрешенный TUN должен закрыть тот же запрос: иначе успешный тест не доказывает работу pf.
            pf.Enable(policy with { TunnelInterface = null });
            try
            {
                await Page();
                Assert.Fail("pf без разрешенного TUN пропустил HTTP: якорь не действует");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
            {
                // Ожидаемый отказ: тестовый TUN закрыт, остальная сеть машины не затронута.
            }
        }
        finally
        {
            try
            {
                pf.Disable();
            }
            finally
            {
                Pfctl(["-a", parent, "-F", "all"], null);
                await singBox.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                await xray.StopAsync(cancellationToken: CancellationToken.None);
            }
        }

        async Task WaitForXray()
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            stop.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                using var socket = new TcpClient();
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, link.Port, stop.Token);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(50, stop.Token);
                }
            }
        }

        async Task<string> Page()
        {
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            return await http.GetStringAsync(new Uri($"http://{address}/"), cancellation);
        }

        async Task<bool> Dns()
        {
            // Один вопрос A для example.com. Контролируемый DNS теста возвращает один адрес последними четырьмя байтами.
            byte[] query = [0x50, 0x47, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 7, 101, 120, 97, 109, 112, 108, 101, 3, 99, 111, 109, 0, 0, 1, 0, 1];
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            stop.CancelAfter(TimeSpan.FromSeconds(5));
            await udp.SendAsync(query, new IPEndPoint(subnet.Dns, 53), stop.Token);
            var reply = (await udp.ReceiveAsync(stop.Token)).Buffer;
            reply.Length.ShouldBeGreaterThan(query.Length);
            reply.AsSpan(0, 2).ToArray().ShouldBe(query.AsSpan(0, 2).ToArray());
            new IPAddress(reply.AsSpan(reply.Length - 4)).ShouldBe(address);
            dns.Queries.ShouldContain("example.com");
            return true;
        }
    }
}
