using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed class DiagnosticProxyTests
{
    private static readonly byte[] NoAuthentication = [5, 1, 0];

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Only_the_authenticated_request_to_the_pinned_profile_host_and_port_can_open_a_service_socket(
        bool authenticated, bool allowedHost, bool allowedPort)
    {
        using var site = new LocalSite("diagnostic reply");
        var dialled = new List<IPEndPoint>();
        var allowed = new Dictionary<TransportEndpoint, IReadOnlyList<IPAddress>>
        {
            [new TransportEndpoint("pinned.example.com", site.Url.Port)] = [IPAddress.Loopback],
            [new TransportEndpoint("PINNED.EXAMPLE.COM.", site.Url.Port)] = [IPAddress.Loopback.MapToIPv6()],
        };
        await using var proxy = new DiagnosticProxy(allowed, "test-interface", 1234, TimeSpan.FromSeconds(5),
            async (endpoint, adapter, mark, token) =>
            {
                adapter.ShouldBe("test-interface");
                mark.ShouldBe(1234u);
                dialled.Add(endpoint);
                return await DiagnosticProxy.ConnectMarkedAsync(endpoint, null, 0, token);
            });
        allowed.Clear();
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{proxy.Upstream.Port}"))
            {
                Credentials = new NetworkCredential(proxy.Upstream.Account.User,
                    authenticated ? proxy.Upstream.Account.Password : "wrong-test-password"),
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var host = allowedHost ? "pinned.example.com" : "not-pinned.example.com";
        var port = allowedPort ? site.Url.Port : XrayKit.FreePort();
        var request = http.GetStringAsync(new Uri($"http://{host}:{port}/"), TestContext.Current.CancellationToken);

        if (authenticated && allowedHost && allowedPort)
        {
            (await request).ShouldBe("diagnostic reply");
            dialled.ShouldBe([new IPEndPoint(IPAddress.Loopback, site.Url.Port)]);
            site.Requests.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<HttpRequestException>(() => request);
            dialled.ShouldBeEmpty("неверный пароль, другое имя или порт не дают службе открыть никакой внешний сокет");
            site.Requests.ShouldBe(0);
        }
    }

    [Fact]
    public async Task A_slow_first_pinned_address_leaves_time_to_try_the_second_without_resolving_the_name_again()
    {
        using var site = new LocalSite("second pinned address");
        var slow = IPAddress.Parse("198.51.100.10");
        var tried = new List<IPAddress>();
        await using var proxy = new DiagnosticProxy(new Dictionary<TransportEndpoint, IReadOnlyList<IPAddress>>
        {
            [new TransportEndpoint("pinned.example.com", site.Url.Port)] = [slow, IPAddress.Loopback],
        }, null, 0, TimeSpan.FromSeconds(2), async (endpoint, adapter, mark, token) =>
        {
            tried.Add(endpoint.Address);
            if (endpoint.Address.Equals(slow))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await DiagnosticProxy.ConnectMarkedAsync(endpoint, null, 0, token);
        });
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{proxy.Upstream.Port}"))
            {
                Credentials = new NetworkCredential(proxy.Upstream.Account.User, proxy.Upstream.Account.Password),
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        (await http.GetStringAsync(new Uri($"http://pinned.example.com:{site.Url.Port}/"), TestContext.Current.CancellationToken))
            .ShouldBe("second pinned address");
        tried.ShouldBe([slow, IPAddress.Loopback]);
    }

    [Fact]
    public async Task The_bridge_never_offers_an_unauthenticated_proxy()
    {
        var dialled = 0;
        await using var proxy = new DiagnosticProxy(new Dictionary<TransportEndpoint, IReadOnlyList<IPAddress>>
        {
            [new TransportEndpoint("example.com", 443)] = [IPAddress.Parse("203.0.113.1")],
        }, null, 0, TimeSpan.FromSeconds(5), (endpoint, adapter, mark, token) =>
        {
            Interlocked.Increment(ref dialled);
            throw new InvalidOperationException("неаутентифицированный клиент не должен дойти до соединения");
        });
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Upstream.Port, TestContext.Current.CancellationToken);
        await client.GetStream().WriteAsync(NoAuthentication, TestContext.Current.CancellationToken);
        var answer = new byte[2];
        await client.GetStream().ReadExactlyAsync(answer, TestContext.Current.CancellationToken);
        answer.ShouldBe(new byte[] { 5, 255 });
        dialled.ShouldBe(0);
    }

    [Fact]
    public async Task Disposal_closes_the_waiting_stream_and_listener_without_waiting_for_the_remote_timeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var dialled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var proxy = new DiagnosticProxy(new Dictionary<TransportEndpoint, IReadOnlyList<IPAddress>>
            {
                [new TransportEndpoint("pinned.example.com", port)] = [IPAddress.Loopback],
            }, null, 0, TimeSpan.FromSeconds(30), async (endpoint, adapter, mark, token) =>
            {
                var socket = await DiagnosticProxy.ConnectMarkedAsync(endpoint, null, 0, token);
                dialled.TrySetResult();
                return socket;
            });
            using var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{proxy.Upstream.Port}"))
                {
                    Credentials = new NetworkCredential(proxy.Upstream.Account.User, proxy.Upstream.Account.Password),
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            var request = http.GetStringAsync(new Uri($"http://pinned.example.com:{port}/"), TestContext.Current.CancellationToken);
            await dialled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Should.ThrowAsync<HttpRequestException>(() => request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            using var after = new TcpClient();
            await Should.ThrowAsync<SocketException>(() => after.ConnectAsync(IPAddress.Loopback, proxy.Upstream.Port,
                TestContext.Current.CancellationToken).AsTask());
        }
        finally
        {
            listener.Stop();
        }
    }
}
