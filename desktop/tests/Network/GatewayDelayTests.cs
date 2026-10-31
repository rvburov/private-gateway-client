using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// «Задержка» (решение 509): до сервера, через сервер по открытому соединению и по новому — медианы трех замеров через
/// настоящее ядро Xray и свой сервер Xray на 127.0.0.1.
/// </summary>
public sealed class GatewayDelayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Median_is_the_middle_sample_and_the_mean_of_two_middles()
    {
        Gateway.Median([30, 400, 31]).ShouldBe(31, "один выброс медиану не сдвигает");
        Gateway.Median([30, 33]).ShouldBe(32);
        Gateway.Median([5]).ShouldBe(5);
        Gateway.Median([]).ShouldBeNull();
    }

    [Fact]
    public async Task Delay_is_measured_to_the_server_and_through_it_while_connected_and_not_otherwise()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("задержка");
        await using var service = await RunningService.StartAsync(site.Url);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);

        var off = await service.Client.MeasureDelayAsync(new V1.MeasureDelayRequest(), cancellationToken: Ct);
        (off.ToServerMs, off.ThroughServerMs, off.NewConnectionMs).ShouldBe((0, 0, 0), "без подключения мерить нечего");

        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);
        var requests = site.Requests;

        var delay = await service.Client.MeasureDelayAsync(new V1.MeasureDelayRequest(), cancellationToken: Ct);

        delay.ToServerMs.ShouldBeGreaterThan(0, "TCP до сервера теста");
        delay.ThroughServerMs.ShouldBeGreaterThan(0, "запрос через ядро и сервер");
        delay.NewConnectionMs.ShouldBeGreaterThan(0, "запрос по новому соединению");
        delay.MeasuredAt.ShouldNotBeNull();
        (site.Requests - requests).ShouldBeGreaterThanOrEqualTo(2 * Gateway.DelaySamples + 1, "по три запроса через сервер на каждое число и прогрев");
    }
}
