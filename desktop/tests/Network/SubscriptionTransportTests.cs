using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящий HTTP через парольный SOCKS на петле: имя назначения не разрешается системой, прямого повтора нет.</summary>
public sealed class SubscriptionTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_subscription_and_redirect_use_the_authenticated_core_even_for_an_unresolvable_name()
    {
        using var proxy = new SocksEndpoint();
        var link = new ProbeLink(proxy.Port, "session-user", "session-password");
        var first = proxy.AnswerAsync("HTTP/1.1 302 Found\r\nLocation: http://redirect.example.invalid/sub/fake-token\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        var pending = SubscriptionTransport.DownloadAsync(link, new Uri("http://subscription.example.invalid/sub/fake-token"), TimeProvider.System, Ct);
        (await first).ShouldBe(("session-user", "session-password", "subscription.example.invalid"));
        var second = proxy.AnswerAsync("HTTP/1.1 200 OK\r\nProfile-Title: Test\r\nContent-Length: 4\r\nConnection: close\r\n\r\nbody"u8.ToArray());
        var result = await pending;

        (await second).ShouldBe(("session-user", "session-password", "redirect.example.invalid"));
        result.Failure.ShouldBeNull();
        Encoding.UTF8.GetString(result.Content!).ShouldBe("body");
        result.Title.ShouldBe("Test");
    }

    [Fact]
    public async Task A_dead_core_never_falls_back_to_the_direct_destination()
    {
        using var site = new LocalSite("must-not-be-read");
        var result = await SubscriptionTransport.DownloadAsync(new ProbeLink(XrayKit.FreePort(), "session-user", "session-password"), site.Url, TimeProvider.System, Ct);

        result.Failure.ShouldNotBeNull();
        result.Content.ShouldBeNull();
        site.Requests.ShouldBe(0, "даже локальный адрес идет через фиксированный SOCKS; прямой повтор запрещен");
    }

    [Fact]
    public async Task The_limit_is_checked_after_decompression()
    {
        using var proxy = new SocksEndpoint();
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(new byte[SubscriptionDownload.MaxBytes + 1]);
        }

        var body = compressed.ToArray();
        var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var serving = proxy.AnswerAsync([.. headers, .. body]);
        var result = await SubscriptionTransport.DownloadAsync(new ProbeLink(proxy.Port, "user", "password"), new Uri("http://example.invalid/sub/fake-token"), TimeProvider.System, Ct);
        await serving;

        result.Failure.ShouldBe(DownloadFailure.TooLarge);
        result.Content.ShouldBeNull();
    }

    private sealed class SocksEndpoint : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public SocksEndpoint() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Dispose() => _listener.Stop();

        public async Task<(string User, string Password, string Host)> AnswerAsync(byte[] response)
        {
            using var client = await _listener.AcceptTcpClientAsync(Ct);
            await using var stream = client.GetStream();
            var hello = await Read(stream, 2);
            hello[0].ShouldBe((byte)5);
            await Read(stream, hello[1]);
            await stream.WriteAsync(new byte[] { 5, 2 }, Ct);
            var auth = await Read(stream, 2);
            auth[0].ShouldBe((byte)1);
            var user = Encoding.UTF8.GetString(await Read(stream, auth[1]));
            var password = Encoding.UTF8.GetString(await Read(stream, (await Read(stream, 1))[0]));
            await stream.WriteAsync(new byte[] { 1, 0 }, Ct);
            var connect = await Read(stream, 4);
            connect[0].ShouldBe((byte)5);
            var host = connect[3] switch
            {
                3 => Encoding.UTF8.GetString(await Read(stream, (await Read(stream, 1))[0])),
                1 => new IPAddress(await Read(stream, 4)).ToString(),
                4 => new IPAddress(await Read(stream, 16)).ToString(),
                _ => throw new InvalidDataException("неизвестный вид адреса SOCKS"),
            };
            await Read(stream, 2);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, Ct);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(Ct) is { Length: > 0 })
            {
            }

            await stream.WriteAsync(response, Ct);
            return (user, password, host);
        }

        private static async Task<byte[]> Read(Stream stream, int count)
        {
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, Ct);
            return bytes;
        }
    }
}
