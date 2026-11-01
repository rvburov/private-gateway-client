using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class VpnTunnelTests
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vless-xhttp")]
    public async Task Another_port_of_the_vpn_server_stays_in_the_tunnel_with_or_without_filters(string transport)
    {
        SkipWithoutNamespaces();
        NetworkNamespace.SkipUnlessAvailable("nft");
        using var netns = NetworkNamespace.Create();
        var (address, _, _) = netns.LinkAsMachine();
        using var site = new LocalSite(EndpointTunnelFixture.TunneledPage);
        using var dns = new TestDns(IPAddress.Parse("203.0.113.7"));
        using var direct = new LocalSite(EndpointTunnelFixture.DirectPage, address);
        (await netns.CurlAsync(direct.Url)).ShouldBe(EndpointTunnelFixture.DirectPage, "до TUN сайт на том же IP доступен напрямую");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair(transport, serverPort, address);
        EndpointTunnelFixture.RouteSite(serverConfig, address, site.Url.Port, dns.Port);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition, ConnectionMode.Vpn,
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug }, RoutingSettings.Default);
        var output = new CoreOutput(netns);
        var link = VpnTunnel.NewLink(address);
        var clientConfig = XrayConfigBuilder.Build(request, link).Config.ShouldNotBeNull();
        EndpointTunnelFixture.AllowTestAddress(clientConfig, address);
        using var xray = CoreProcess.StartXray(XrayKit.Xray!, clientConfig, netns.Path, line => output.Add("xray", line));
        await WaitEndpointAsync(() => EndpointListens(netns, link.Port), "вход Xray", output);
        var ipv6 = File.Exists("/proc/sys/net/ipv6/conf/default/disable_ipv6") && File.ReadAllText("/proc/sys/net/ipv6/conf/default/disable_ipv6").Trim() == "0";
        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(link, [address], serverPort, request.Protection, request.Connection,
            new TunIpv6(Kernel: ipv6, Tunnel: false)));
        var started = 0;
        using var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, netns.Path, line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started, 1);
            }

            output.Add("sing-box", line);
        });
        var killSwitch = new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments]));
        try
        {
            await WaitEndpointAsync(() => Volatile.Read(ref started) == 1, "sing-box", output);
            // Первый прикладной запрос создает и внешнее соединение после установки маршрутов TUN: исключение по порту
            // должно вывести новый transport-сокет на настоящую пару veth без петли. Прямой сайт отвечает иначе.
            foreach (var filtered in new[] { true, false })
            {
                if (filtered)
                {
                    killSwitch.Enable(new KillSwitchPolicy(SingBoxConfigBuilder.InterfaceName, [new(address, serverPort)]));
                    killSwitch.IsIntact.ShouldBeTrue();
                }
                else
                {
                    killSwitch.Disable();
                }

                (await output.TraceAsync(() => netns.CurlAsync(direct.Url)))
                    .ShouldBe(EndpointTunnelFixture.TunneledPage, "соседний порт IP сервера остается в TUN и с фильтрами, и без них");
                direct.Requests.ShouldBe(1, "после исходного прямого запроса сайт напрямую больше не посещался");
            }

            site.Requests.ShouldBe(2);
        }
        finally
        {
            killSwitch.Disable();
            await singBox.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await xray.StopAsync(cancellationToken: CancellationToken.None);
        }

        killSwitch.IsEnabled.ShouldBeFalse();
        (await netns.CurlAsync(direct.Url)).ShouldBe(EndpointTunnelFixture.DirectPage, "после снятия TUN вернулся исходный прямой маршрут");
    }

    private static bool EndpointListens(NetworkNamespace netns, int port)
    {
        try
        {
            return netns.Run(null, "python3", "-c", "import socket,sys; socket.create_connection(('127.0.0.1',int(sys.argv[1])),1).close()",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture)).Length == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitEndpointAsync(Func<bool> ready, string what, CoreOutput output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline)
            {
                await output.TraceAsync<bool>(() => throw new InvalidOperationException("Не дождались: " + what));
            }

            await Task.Delay(100, Ct);
        }
    }
}
