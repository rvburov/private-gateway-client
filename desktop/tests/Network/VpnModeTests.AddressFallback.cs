using System.Net;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class VpnModeTests
{
    [Fact]
    public async Task Reconnect_tries_the_other_resolved_address_without_widening_firewall_endpoints()
    {
        await using var rig = await Rig.CreateAsync();
        var first = IPAddress.Parse(rig.Definition.Host);
        // На соседнем адресе машины есть только сайт, а на транспортном порту VPN никто не слушает.
        var second = IPAddress.Parse(rig.Lan.Url.Host);
        var endpoints = new[] { new KillSwitchEndpoint(first, rig.Definition.Port), new KillSwitchEndpoint(second, rig.Definition.Port) };
        var probes = 0;
        var network = new FakeNetwork();
        var hour = TimeSpan.FromHours(1);
        await using var service = await RunningService.StartAsync(new Uri("http://1.1.1.1/"), options: o => rig.Vpn(o) with
        {
            Resolve = (_, _) => Task.FromResult<IPAddress[]>([first, second]),
            Network = network,
            Reconnect = new ReconnectTiming(hour, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), hour),
            // Подготовка здесь не предмет теста; проба работающего ядра ниже использует настоящий протокол в пространстве.
            Preflight = (_, _, _) => Task.FromResult(ProbeReachability.Responded(HttpStatusCode.OK)),
            Probe = (probe, token) => Interlocked.Increment(ref probes) is 2 or 3 ? Task.FromResult(false) : rig.ProbeAsync(probe, token),
        });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(VpnRequest(rig.Definition with { Host = "vpn.example.com" }), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        // Два отказа вызывают переподключение. Сначала другой адрес действительно не отвечает через Xray;
        // следующая попытка возвращается к первому и проводит запрос по настоящему туннелю.
        network.Raise();
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Reconnecting && s.Attempt >= 2, service.Journal);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);

        (await rig.Netns.CurlAsync(new Uri("http://1.1.1.1/"))).ShouldBe("через туннель");
        var policies = rig.KillSwitch.Policies;
        policies.ShouldContain(p => p.Servers.Count > 0 && p.Servers[0].Address.Equals(second),
            "первый повтор должен реально попробовать другой разрешенный адрес");
        foreach (var policy in policies)
        {
            policy.Servers.ShouldBe(endpoints, ignoreOrder: true,
                "перебор меняет порядок попыток, а не разрешенный firewall набор IP и портов");
        }
        rig.KillSwitch.Last.ShouldNotBeNull().Servers[0].Address.ShouldBe(first);
        service.Gateway.Status.ServerId.ShouldBe("srv-1", "меняется IP того же профиля, другой сервер сам не выбирается");
    }
}
