using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed class GatewayLatencyDiagnosticsTests
{
    [Fact]
    public async Task A_failed_profile_keeps_its_request_position_and_both_safe_probe_details()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var target = new Uri(site.Url, "/private-health-path");
        await using var service = await RunningService.StartAsync(latencyTarget: target);
        var serverPort = XrayKit.FreePort();
        var (alive, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var failed = alive with
        {
            Name = "private-profile-name",
            Credentials = new ServerCredentials(Uuid: "99999999-2222-3333-4444-555555555555"),
        };
        var request = new V1.MeasureLatencyRequest();
        request.Targets.Add(new V1.LatencyTarget { Id = "invalid-private-profile-id" });
        request.Targets.Add(new V1.LatencyTarget { Id = "private-profile-id", Server = failed.ToContract("private-profile-id") });

        var response = await service.Gateway.MeasureLatencyAsync(request, TestContext.Current.CancellationToken);

        response.Results.Count.ShouldBe(2);
        var log = service.Journal.Tail(ServiceJournal.Capacity).Where(entry => entry.Message.StartsWith("Замер профиля #", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        log.Level.ShouldBe(JournalLevel.Warning);
        log.Message.ShouldContain("#2 (Vless/Tcp/None; отдельное ядро замера; при старте Vpn/Off)");
        log.Message.ShouldContain("попытка 1:");
        log.Message.ShouldContain("попытка 2:");
        foreach (var secret in new[] { failed.Name, failed.Host, failed.Credentials.Uuid!, "private-profile-id", "private-health-path" })
        {
            log.Message.ShouldNotContain(secret);
        }
    }
}
