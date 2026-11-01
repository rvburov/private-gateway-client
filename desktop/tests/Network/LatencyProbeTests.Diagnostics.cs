using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class LatencyProbeTests
{
    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, ProbeFailure.NameResolution)]
    [InlineData(HttpRequestError.SecureConnectionError, ProbeFailure.Tls)]
    [InlineData(HttpRequestError.ConnectionError, ProbeFailure.Connection)]
    [InlineData(HttpRequestError.ProxyTunnelError, ProbeFailure.Proxy)]
    [InlineData(HttpRequestError.UserAuthenticationError, ProbeFailure.Authentication)]
    [InlineData(HttpRequestError.InvalidResponse, ProbeFailure.Protocol)]
    [InlineData(HttpRequestError.ResponseEnded, ProbeFailure.Protocol)]
    [InlineData(HttpRequestError.Unknown, ProbeFailure.Network)]
    public void Probe_failure_keeps_the_reason_but_not_exception_text(HttpRequestError error, ProbeFailure expected)
    {
        var failure = ProbeReachability.Failed(new HttpRequestException(error, "https://private.example.com/sub/do-not-log-me",
            new IOException("user-and-password-must-not-be-logged", new SocketException((int)SocketError.ConnectionRefused))));

        failure.Reached.ShouldBeFalse();
        failure.StatusCode.ShouldBeNull();
        failure.Failure.ShouldBe(expected);
        failure.HttpError.ShouldBe(error);
        failure.SocketFailure.ShouldBe(SocketError.ConnectionRefused);
        failure.Description.ShouldContain(error.ToString());
        failure.Description.ShouldContain(nameof(SocketError.ConnectionRefused));
        failure.Description.ShouldNotContain("private.example.com");
        failure.ToString().ShouldNotContain("do-not-log-me");
        failure.ToString().ShouldNotContain("user-and-password");
    }

    [Theory]
    [InlineData("204 No Content", 204)]
    [InlineData("503 Service Unavailable", 503)]
    public async Task Any_http_response_from_the_core_counts_as_reachable_and_keeps_its_status(string status, int code)
    {
        using var proxy = new ProbeSocks();
        var serving = proxy.ServeAsync($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var result = await LatencyProbe.CheckAsync(new ServerProbe(proxy.Port, null,
            new Uri("http://probe.example.com/private-path"), TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);
        await serving;

        result.Reached.ShouldBeTrue("даже HTTP 503 отвечает по рабочему пути и не должен вызывать переподключение VPN");
        result.StatusCode.ShouldBe(code);
        result.Failure.ShouldBeNull();
        result.Description.ShouldContain("HTTP " + code);
        result.Description.ShouldNotContain("probe.example.com");
        result.Description.ShouldNotContain("private-path");
    }

    [Fact]
    public async Task A_core_that_accepts_the_request_but_does_not_reply_is_reported_as_timeout()
    {
        using var proxy = new ProbeSocks();
        var serving = proxy.ServeAsync(null);
        var result = await LatencyProbe.CheckAsync(new ServerProbe(proxy.Port, null,
            new Uri("http://probe.example.com/"), TimeSpan.FromSeconds(1)), TestContext.Current.CancellationToken);
        await serving;

        proxy.RequestRead.Task.IsCompletedSuccessfully.ShouldBeTrue("SOCKS принял запрос, а HTTP-ответ не пришел");
        result.Reached.ShouldBeFalse();
        result.Failure.ShouldBe(ProbeFailure.Timeout);
        result.Description.ShouldContain("время ожидания");
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_reported_as_a_server_timeout()
    {
        using var proxy = new ProbeSocks();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var serving = proxy.ServeAsync(null);
        var check = LatencyProbe.CheckAsync(new ServerProbe(proxy.Port, null,
            new Uri("http://probe.example.com/"), TimeSpan.FromSeconds(30)), stop.Token);
        await proxy.RequestRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => check);
        await serving;
    }

    [Fact]
    public async Task A_dead_probe_core_has_a_reason_and_never_retries_the_destination_directly()
    {
        using var site = new LocalSite("must-not-be-read");
        var result = await LatencyProbe.CheckAsync(new ServerProbe(XrayKit.FreePort(), null, site.Url, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

        result.Reached.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.HttpError.ShouldNotBeNull();
        site.Requests.ShouldBe(0, "localhost назначения тоже идет через SOCKS, а не напрямую после отказа");
    }

    /// <summary>Свой SOCKS без авторизации: HTTP-ответ или ожидание закрытия клиента; все ожидания ограничены.</summary>
    private sealed class ProbeSocks : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public ProbeSocks() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public TaskCompletionSource RequestRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        public async Task ServeAsync(string? response)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var hello = await Read(stream, 2, ct);
            hello[0].ShouldBe((byte)5);
            await Read(stream, hello[1], ct);
            await stream.WriteAsync(new byte[] { 5, 0 }, ct);
            var connect = await Read(stream, 4, ct);
            connect[3].ShouldBe((byte)3, "имя передается SOCKS-серверу, DNS машины не нужен");
            Encoding.UTF8.GetString(await Read(stream, (await Read(stream, 1, ct))[0], ct)).ShouldBe("probe.example.com");
            await Read(stream, 2, ct);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, ct);
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(ct) is { Length: > 0 })
            {
            }

            RequestRead.TrySetResult();
            if (response is not null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct);
            }
            else
            {
                try
                {
                    while (await stream.ReadAsync(new byte[1], ct) > 0)
                    {
                    }
                }
                catch (IOException)
                {
                    // Проверка закрыла соединение по отмене или сроку.
                }
            }
        }

        private static async Task<byte[]> Read(Stream stream, int count, CancellationToken ct)
        {
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, ct);
            return bytes;
        }
    }
}
