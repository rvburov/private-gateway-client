using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class WindowsTunnelTests
{
    [Theory]
    [InlineData("vless")]
    [InlineData("vless-xhttp")]
    [SupportedOSPlatform("windows")]
    public async Task Another_port_of_the_vpn_server_stays_in_the_tunnel_with_or_without_filters(string transport)
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.Xray is null || XrayKit.SingBox is null, "нет Xray или sing-box — scripts/get-xray.sh, scripts/get-singbox.sh");
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        Assert.SkipUnless(File.Exists(curl), "нет curl.exe");
        var address = IPAddress.Parse("198.51.100.254");
        using var site = new LocalSite(EndpointTunnelFixture.TunneledPage);
        using var dns = new TestDns(IPAddress.Parse("203.0.113.7"));
        using var direct = new LocalSite(EndpointTunnelFixture.DirectPage);
        var page = new Uri($"http://{address}:{direct.Url.Port}/");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair(transport, serverPort);
        EndpointTunnelFixture.RouteSite(serverConfig, address, site.Url.Port, dns.Port);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var request = new CoreRequest(definition with { Host = address.ToString() }, ConnectionMode.Vpn,
            ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "8.8.8.8" },
            ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug }, RoutingSettings.Default);
        var output = new CoreOutput();
        var link = VpnTunnel.NewLink(address);
        var clientConfig = XrayConfigBuilder.Build(request, link).Config.ShouldNotBeNull();
        EndpointTunnelFixture.AllowTestAddress(clientConfig, address);
        using var xray = CoreProcess.StartXray(XrayKit.Xray!, clientConfig, output: line => output.Add("xray", line));
        await WaitAsync(() => Listens(link.Port), "вход Xray", output);
        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(link, [address], serverPort, request.Protection, request.Connection,
            new TunIpv6(Kernel: true, Tunnel: false), Routes: [EndpointTunnelFixture.Cidr(address)]));
        // Только транспортный endpoint стенда находится на localhost. Матч IP и порта — из production-конфига, меняется
        // лишь адрес конечного сокета: настоящий TUN захватывает соединение Xray к TESTNET и возвращает его серверу теста.
        // auto_detect_interface остается включен; loopback для этого адреса выбирает sing-box. Удаленный NIC здесь не проверяется.
        var endpoint = tun["route"]!["rules"]!.AsArray().Single(rule => rule!["outbound"]?.GetValue<string>() == SingBoxConfigBuilder.ServerDirectTag)!;
        endpoint["override_address"] = IPAddress.Loopback.ToString();
        var started = 0;
        using var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, output: line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started, 1);
            }

            output.Add("sing-box", line);
        });
        var killSwitch = new WfpKillSwitch(onlyProgram: curl);
        try
        {
            await WaitAsync(() => Volatile.Read(ref started) == 1 && Adapter() is not null, "sing-box и адаптер", output);
            // Первый запрос — только после TUN: у Xray еще нет внешнего соединения, прогретый XHTTP не скроет петлю.
            // Сначала с WFP: старое IP-wide исключение откажет до отправки TESTNET-пакета в сеть машины CI.
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

                var response = await output.TraceAsync(() => GetEndpointWithCurlAsync(curl, page));
                response.ShouldBe(EndpointTunnelFixture.TunneledPage, "соседний порт IP сервера — через TUN, без прямого разрешения в WFP");
                direct.Requests.ShouldBe(0, "слишком широкое direct-правило вывело бы запрос на прямой сайт стенда");
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
        await WaitAsync(() => Adapter() is null, "адаптер убран вместе с sing-box", output);
    }

    private static async Task<string> GetEndpointWithCurlAsync(string curl, Uri page)
    {
        var start = new ProcessStartInfo(curl) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-q", "--noproxy", "*", "--silent", "--show-error", "--max-time", "10", page.ToString() })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"curl завершился с кодом {process.ExitCode}: {await error}");
        }

        return await output;
    }
}
