using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Import;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Подписка пакета цепочки: DNS имени назначения остаётся у SOCKS и нет прямого fallback.</summary>
public sealed class EntrySubscriptionDownloaderTests
{
    [Fact]
    public async Task Entry_subscription_uses_socks_connect_and_does_not_try_the_ordinary_downloader_after_refusal()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var url = new Uri("https://subscription.example.invalid/sub/entry/test-token");
        var entry = new ServerDefinition("Entry", "", ProxyProtocol.Socks5, "127.0.0.1", port,
            TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials());
        var ordinary = new CountedDownloader();
        var downloader = new EntrySubscriptionDownloader(ordinary, new FixedEntryStore(url, entry), TimeProvider.System);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));

        var fetching = downloader.DownloadAsync(url, deadline.Token);
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        var stream = client.GetStream();
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, deadline.Token);
        greeting[0].ShouldBe((byte)5);
        var methods = new byte[greeting[1]];
        await stream.ReadExactlyAsync(methods, deadline.Token);
        methods.ShouldContain((byte)0);
        await stream.WriteAsync(new byte[] { 5, 0 }, deadline.Token);

        var connect = new byte[4];
        await stream.ReadExactlyAsync(connect, deadline.Token);
        connect.ShouldBe(new byte[] { 5, 1, 0, 3 }, "имя назначения передается SOCKS без локального DNS");
        var length = new byte[1];
        await stream.ReadExactlyAsync(length, deadline.Token);
        var host = new byte[length[0]];
        await stream.ReadExactlyAsync(host, deadline.Token);
        Encoding.ASCII.GetString(host).ShouldBe("subscription.example.invalid");
        var destinationPort = new byte[2];
        await stream.ReadExactlyAsync(destinationPort, deadline.Token);
        destinationPort.ShouldBe(new byte[] { 1, 187 });
        await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, deadline.Token);

        var failure = await Should.ThrowAsync<SubscriptionDownloadException>(() => fetching);
        failure.Failure.ShouldBe(DownloadFailure.Connection);
        failure.InnerException.ShouldBeNull("токен URL не должен попасть в цепочку исключений");
        ordinary.Calls.ShouldBe(0, "после отказа SOCKS прямая загрузка запрещена");
    }

    private sealed class CountedDownloader : ISubscriptionDownloader
    {
        public int Calls { get; private set; }

        public Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SubscriptionContent("should not be read", null));
        }
    }

    private sealed class FixedEntryStore(Uri url, ServerDefinition proxy) : IEntryProxyStore
    {
        public ImportOutcome Import(EntryChainBundle bundle, string? groupName) => throw new NotSupportedException();

        public SubscriptionUpdate? Update(string subscriptionId, Uri expectedUrl, EntryChainBundle bundle) =>
            throw new NotSupportedException();

        public ServerDefinition? DefinitionFor(string serverId) => null;

        public EntryChainSubscription? SubscriptionFor(Uri requested) =>
            requested == url ? new EntryChainSubscription("node-test", url, proxy) : null;
    }
}
