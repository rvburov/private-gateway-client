using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящий Xray без удаленного сервера; только петля, без изменения сети машины.</summary>
public sealed class LocalSiteFilterNetworkTests
{
    private sealed class LocalProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => endpoint;

        public bool IsBypassed(Uri host) => false;
    }

    [Fact]
    public async Task Direct_filter_blocks_requested_name_and_captures_access_without_public_journal()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("direct local filter");
        var port = XrayKit.FreePort();
        var unused = new ServerDefinition("unused", string.Empty, ProxyProtocol.Vless, "unused.example.com", 443,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials());
        var request = new CoreRequest(unused, ConnectionMode.Proxy,
            ProtectionSettings.Default with { AllowLan = true },
            ConnectionSettings.Default with { ProxyPort = port },
            RoutingSettings.Default with { Rules = [new DomainRule("blocked.example.com", RuleAction.Block)] },
            LocalSiteFilter: true);
        var config = XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
        var history = new BlockedSiteHistory(TimeProvider.System);
        var journal = new ServiceJournal(TimeProvider.System);
        using var core = await XrayKit.Start(config, port, line =>
        {
            if (!history.Consume(line))
            {
                journal.Add(CoreLines.Level("xray", line), "xray", line);
            }
        });
        using var handler = new SocketsHttpHandler { Proxy = new LocalProxy(new Uri($"socks5://127.0.0.1:{port}")) };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        (await http.GetStringAsync(site.Url, TestContext.Current.CancellationToken)).ShouldBe("direct local filter");
        await Should.ThrowAsync<HttpRequestException>(() => http.GetStringAsync("http://blocked.example.com/", TestContext.Current.CancellationToken));
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (history.Snapshot().Count == 0 && DateTimeOffset.UtcNow < until)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        history.Snapshot().ShouldHaveSingleItem().Host.ShouldBe("blocked.example.com");
        journal.Tail(ServiceJournal.Capacity).ShouldAllBe(entry => !entry.Message.Contains("blocked.example.com", StringComparison.Ordinal)
            && !entry.Message.Contains(" accepted ", StringComparison.Ordinal));
        VpnTunnel.DirectPrograms(request, "/tmp/test-xray").ShouldContain(Path.GetFullPath("/tmp/test-xray"));
    }
}
