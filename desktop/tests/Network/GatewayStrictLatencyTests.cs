using System.Net;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed class GatewayStrictLatencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-strict-latency-" + Guid.NewGuid().ToString("N"));

    public GatewayStrictLatencyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_fresh_profile_probe_under_the_strict_block_keeps_user_traffic_closed_and_restores_the_original_policy(bool byName, bool mapped)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var port = XrayKit.FreePort();
        var (original, config) = XrayKit.Pair(byName ? "vless-ws-tls" : "vless", port);
        using var server = await XrayKit.Start(config, port);
        var candidate = byName ? original with { Host = "candidate.example.com" } : mapped ? original with { Host = "::ffff:127.0.0.1" } : original;
        var firewall = new Firewall();
        var policies = firewall.Policies;
        var names = new List<string>();
        var sockets = new List<IPEndPoint>();
        var boot = Path.Combine(_root, "boot.json");
        var preserved = new KillSwitchPolicy(null, [new KillSwitchEndpoint(IPAddress.Parse("198.51.100.9"), 8443)], Persistent: true);
        BootBlock.Save(boot, new HeldBlock(preserved, "previous.example.com", Boot: true, Strict: true));
        await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, site.Url,
            KillSwitch: firewall, BootBlock: boot, Doh: new Uri("https://1.1.1.1/dns-query"),
            Resolve: (_, _) => throw new InvalidOperationException("DNS ОС под блоком использовать нельзя"),
            LookupPastBlock: (_, host, mark, _) =>
            {
                names.Add(host);
                mark.ShouldBe(Gateway.LookupMark);
                firewall.Last.Lookup.ShouldNotBeNull().Endpoints.ShouldBe([new KillSwitchEndpoint(IPAddress.Parse("1.1.1.1"), 443)]);
                return Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Loopback]);
            },
            DiagnosticConnect: async (endpoint, adapter, mark, token) =>
            {
                mark.ShouldBe(Gateway.DiagnosticMark);
                var permission = firewall.Last.Lookup.ShouldNotBeNull();
                permission.Mark.ShouldBe(Gateway.DiagnosticMark);
                permission.Endpoints.ShouldContain(new KillSwitchEndpoint(endpoint.Address, endpoint.Port));
                permission.Endpoints.ShouldNotContain(new KillSwitchEndpoint(IPAddress.Loopback, site.Url.Port),
                    "сама служба открывает только транспорт профиля, а не проверочный сайт в обход него");
                sockets.Add(endpoint);
                return await DiagnosticProxy.ConnectMarkedAsync(endpoint, null, 0, token);
            }), TimeProvider.System, new ServiceJournal(TimeProvider.System));
        var request = new V1.MeasureLatencyRequest();
        request.Targets.Add(new V1.LatencyTarget { Id = "candidate", Server = candidate.ToContract("candidate") });

        var reply = await gateway.MeasureLatencyAsync(request, TestContext.Current.CancellationToken);

        reply.Results.ShouldHaveSingleItem().Reachable.ShouldBeTrue();
        reply.Results[0].Route.ShouldBe("direct");
        reply.Results[0].CheckedAtUnixMs.ShouldBeGreaterThan(0);
        gateway.Status.State.ShouldBe(ConnectionState.Blocked);
        gateway.Status.StrictBlock.ShouldBeTrue();
        firewall.Disables.ShouldBe(0);
        firewall.Enabled.ShouldBeTrue();
        firewall.Last.Lookup.ShouldBeNull();
        firewall.Last.Servers.ShouldBe(preserved.Servers);
        policies.ShouldAllBe(policy => policy.Servers.SequenceEqual(preserved.Servers), "общего разрешения адресам не добавляли");
        sockets.ShouldBe([new IPEndPoint(IPAddress.Loopback, port)]);
        names.ShouldBe(byName ? ["candidate.example.com"] : []);
        site.Requests.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_a_new_user_intent_under_the_strict_block_closes_the_bridge_and_restores_permissions(bool newIntent)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var candidate = XrayKit.Pair("vless", ((IPEndPoint)listener.LocalEndpoint).Port).Server;
            var firewall = new Firewall();
            var boot = Path.Combine(_root, "boot.json");
            BootBlock.Save(boot, new HeldBlock(new KillSwitchPolicy(null, [], Persistent: true), "previous.example.com", Boot: true, Strict: true));
            var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, true, new Uri("https://example.com/"),
                KillSwitch: firewall, BootBlock: boot, DiagnosticConnect: async (endpoint, adapter, mark, token) =>
                {
                    var socket = await DiagnosticProxy.ConnectMarkedAsync(endpoint, null, 0, token);
                    opened.TrySetResult();
                    return socket;
                }), TimeProvider.System, new ServiceJournal(TimeProvider.System));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var request = new V1.MeasureLatencyRequest();
            request.Targets.Add(new V1.LatencyTarget { Id = "silent", Server = candidate.ToContract("silent") });
            var measuring = gateway.MeasureLatencyAsync(request, stop.Token);
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            if (newIntent)
            {
                await gateway.DisconnectAsync(keepBlock: true, TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                (await measuring.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Results.ShouldBeEmpty();
            }
            else
            {
                await stop.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(() => measuring.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            }

            firewall.Last.Lookup.ShouldBeNull();
            firewall.Last.Servers.ShouldBeEmpty();
            firewall.Disables.ShouldBe(0);
            gateway.Status.State.ShouldBe(ConnectionState.Blocked);
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Firewall : IKillSwitch
    {
        public string Name => "diagnostic test firewall";
        public bool Enabled { get; private set; } = true;
        public bool IsEnabled => Enabled;
        public int Disables { get; private set; }
        public List<KillSwitchPolicy> Policies { get; } = [];
        public KillSwitchPolicy Last => Policies[^1];
        public void Enable(KillSwitchPolicy policy)
        {
            Policies.Add(policy);
            Enabled = true;
        }
        public void Disable()
        {
            Disables++;
            Enabled = false;
        }
    }
}
