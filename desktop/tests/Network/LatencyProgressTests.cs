using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed class LatencyProgressTests
{
    [Fact]
    public async Task A_queued_stream_result_from_before_a_new_connection_intent_is_not_delivered_as_current()
    {
        await using var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/")),
            TimeProvider.System, new ServiceJournal(TimeProvider.System));
        var request = new V1.MeasureLatencyRequest();
        request.Targets.Add(new V1.LatencyTarget { Id = "first" });
        request.Targets.Add(new V1.LatencyTarget { Id = "queued" });
        await using var results = gateway.MeasureLatencyProgressAsync(request, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        (await results.MoveNextAsync()).ShouldBeTrue();
        results.Current.Id.ShouldBe("first");
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        (await results.MoveNextAsync()).ShouldBeFalse("элемент, уже помещенный в очередь старой сессии, тоже проверяет поколение при чтении");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_protected_candidate_uses_the_authenticated_active_proxy_and_never_falls_back_directly(bool accepted)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var candidatePort = XrayKit.FreePort();
        var (candidate, candidateConfig) = XrayKit.Pair("vless", candidatePort);
        using var candidateServer = await XrayKit.Start(candidateConfig, candidatePort);
        var activePort = XrayKit.FreePort();
        var (_, activeConfig) = ExternalProxyFixture.Pair("socks5", activePort, authenticated: true);
        using var activeServer = await XrayKit.Start(activeConfig, activePort);
        var upstream = new XrayProbeUpstream(activePort, new SocksAccount(ExternalProxyFixture.Username,
            accepted ? ExternalProxyFixture.Password : "intentionally-wrong-test-password"));

        var results = await LatencyProbe.MeasureProgressAsync(XrayKit.Xray!, null, [candidate], ConnectionSettings.Default,
            site.Url, TimeSpan.FromSeconds(5), TimeProvider.System, upstream: upstream, cancellationToken: TestContext.Current.CancellationToken);

        if (accepted)
        {
            results[0].Milliseconds.ShouldNotBeNull();
            results[0].Failure.ShouldBeNull();
            site.Requests.ShouldBe(1);
        }
        else
        {
            results[0].Failure.ShouldBe(LatencyFailure.NoResponse);
            site.Requests.ShouldBe(0, "ошибка защищенного пути не разрешает повтор напрямую к рабочему кандидату");
        }
    }

    [Fact]
    public async Task A_ready_profile_is_published_before_a_silent_profile_and_cancellation_cleans_up_the_measurement()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var alivePort = XrayKit.FreePort();
        var (alive, config) = XrayKit.Pair("vless", alivePort);
        using var server = await XrayKit.Start(config, alivePort);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var silent = alive with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var first = new TaskCompletionSource<LatencyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var measuring = LatencyProbe.MeasureProgressAsync(XrayKit.Xray!, null, [silent, alive], ConnectionSettings.Default,
                site.Url, TimeSpan.FromSeconds(30), TimeProvider.System,
                reportResult: (index, result) => { if (index == 1) { first.TrySetResult(result); } }, cancellationToken: stop.Token);

            var ready = await first.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            ready.Milliseconds.ShouldNotBeNull();
            measuring.IsCompleted.ShouldBeFalse("молчащий профиль не задерживает готовый ответ");
            await stop.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => measuring);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task The_streaming_ipc_returns_each_completed_profile_with_its_measurement_time()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var port = XrayKit.FreePort();
        var (alive, config) = XrayKit.Pair("vless", port);
        using var server = await XrayKit.Start(config, port);
        await using var service = await RunningService.StartAsync(site.Url);
        var request = new V1.MeasureLatencyRequest();
        request.Targets.Add(new V1.LatencyTarget { Id = "first", Server = alive.ToContract("first") });
        request.Targets.Add(new V1.LatencyTarget { Id = "invalid" });
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var call = service.Client.MeasureLatencyProgress(request, cancellationToken: TestContext.Current.CancellationToken);
        var results = new List<V1.LatencyResult>();
        while (await call.ResponseStream.MoveNext(TestContext.Current.CancellationToken))
        {
            results.Add(call.ResponseStream.Current);
        }

        results.Select(result => result.Id).ShouldBe(["invalid", "first"]);
        results[0].Failure.ShouldBe(V1.LatencyFailure.Unsupported);
        results[1].Reachable.ShouldBeTrue();
        results[1].Route.ShouldBe("direct");
        results.ShouldAllBe(result => result.CheckedAtUnixMs >= started);
    }
}
