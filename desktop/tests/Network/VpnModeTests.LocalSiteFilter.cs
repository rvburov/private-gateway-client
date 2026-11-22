using Grpc.Core;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class VpnModeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_filter_uses_direct_internet_without_a_server_blocks_the_sniffed_site_and_restores_on_stop(bool automaticIpCheck)
    {
        await using var rig = await Rig.CreateAsync();
        using var direct = rig.DirectTrace($"fl=1\nip={RealIp}\n");
        rig.StopServer();
        var exitChecks = 0;
        await using var service = await RunningService.StartAsync(options: options => rig.Vpn(options) with
        {
            Trace = Rig.Trace,
            ExitIp = (probe, ct) =>
            {
                Interlocked.Increment(ref exitChecks);
                return rig.ExitIpAsync(probe, ct);
            },
        });
        var capable = new Metadata { { GatewayCapabilities.Header, GatewayCapabilities.LocalSiteFilter } };
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), headers: capable, cancellationToken: Ct);
        var request = VpnRequest(rig.Definition, bootProtection: true,
            routing: RoutingSettings.Default with { Rules = [new DomainRule("blocked.example.com", RuleAction.Block)] });
        request.Server = null;
        request.Protection.StrictMode = true;
        request.Protection.CheckLeaksAfterConnect = automaticIpCheck;
        var accepted = await service.Client.StartSiteFilterAsync(request, cancellationToken: Ct);
        accepted.Accepted.ShouldBeTrue(accepted.Error?.Message);
        var status = (await ServiceIpcTests.ReadUntilAsync(watch, value => value.State == V1.ConnectionState.Connected, service.Journal))[^1].ToCore();
        (status.LocalSiteFilter, status.ServerId, status.Protection.AllTraffic, status.Protection.KillSwitch)
            .ShouldBe((true, null, CheckState.Warning, CheckState.Off));
        var legacy = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).ToCore();
        (legacy.State, legacy.Mode, legacy.ErrorCode, legacy.LocalSiteFilter)
            .ShouldBe((ConnectionState.Error, ConnectionMode.Proxy, ErrorCodes.LocalFilterClientUnsupported, false));
        using (var legacyWatch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct))
        {
            (await legacyWatch.ResponseStream.MoveNext(Ct)).ShouldBeTrue();
            legacyWatch.ResponseStream.Current.ToCore().ShouldBe(legacy);
        }
        var supported = (await service.Client.GetStatusAsync(new V1.GetStatusRequest(), headers: capable, cancellationToken: Ct)).ToCore();
        (supported.State, supported.LocalSiteFilter).ShouldBe((ConnectionState.Connected, true));
        rig.KillSwitch.Last!.Persistent.ShouldBeFalse("локальный фильтр не наследует прежнюю защиту при загрузке");
        (await rig.Netns.CurlAsync(Rig.Trace)).Contains("ip=" + RealIp, StringComparison.Ordinal).ShouldBeTrue("сервер выключен, трафик идет напрямую");
        if (!automaticIpCheck)
        {
            Volatile.Read(ref exitChecks).ShouldBe(0, "местный фильтр не включает отключенные пользователем автоматические IP-проверки");
        }
        await Should.ThrowAsync<InvalidOperationException>(() => rig.Netns.RunAsync("curl", "--noproxy", "*", "-sS", "-m", "3",
            "--resolve", "blocked.example.com:80:1.1.1.1", "http://blocked.example.com/"));
        var history = await service.Client.GetBlockedSitesAsync(new V1.GetBlockedSitesRequest(), cancellationToken: Ct);
        history.Sites.ShouldContain(site => site.Host == "blocked.example.com");
        service.Journal.Tail(ServiceJournal.Capacity).ShouldAllBe(entry => !entry.Message.Contains("blocked.example.com", StringComparison.Ordinal));
        var subscription = (await service.Client.DownloadSubscriptionAsync(new V1.SubscriptionDownloadRequest
        {
            Address = Rig.Trace.AbsoluteUri,
        }, cancellationToken: Ct)).ToCore();
        subscription.Failure.ShouldNotBeNull("локальный фильтр не предоставляет защищенный путь загрузки подписки");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        var stopped = (await ServiceIpcTests.ReadUntilAsync(watch, value => value.State == V1.ConnectionState.Off, service.Journal))[^1];
        stopped.LocalSiteFilter.ShouldBeFalse();
        rig.KillSwitch.IsEnabled.ShouldBeFalse();
        (await rig.Netns.RunAsync("curl", "--noproxy", "*", "-sS", "-m", "3", "--resolve", "blocked.example.com:80:1.1.1.1",
            "http://blocked.example.com/")).Contains("ip=" + RealIp, StringComparison.Ordinal).ShouldBeTrue("после остановки TUN, правила и блок сайтов сняты");
    }
}
