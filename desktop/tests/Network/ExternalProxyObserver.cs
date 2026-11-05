using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Контрольный внешний прокси: принимает имя назначения, не разрешает его, возвращает контрольный IP.</summary>
internal sealed class ExternalProxyObserver : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _kind;
    private readonly X509Certificate2? _certificate;
    private readonly Task _serving;
    private readonly TaskCompletionSource<string> _target = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Task> _clients = [];

    public ExternalProxyObserver(string kind)
    {
        _kind = kind;
        if (kind == "https")
        {
            var pem = JsonNode.Parse(XrayKit.Certificate.Json)!;
            using var imported = X509Certificate2.CreateFromPem(Lines(pem["certificate"]!), Lines(pem["key"]!));
            _certificate = X509CertificateLoader.LoadPkcs12(imported.Export(X509ContentType.Pkcs12), null);
        }

        _listener.Start();
        _serving = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task<string> Target => _target.Task;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving.ConfigureAwait(false);
        await Task.WhenAll(_clients).ConfigureAwait(false);
        _certificate?.Dispose();
        _stop.Dispose();
    }

    private static string Lines(JsonNode values) => string.Join('\n', values.AsArray().Select(line => line!.GetValue<string>()));

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _clients.Add(AnswerAsync(client));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Только собственный слушатель стенда.
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var ssl = _certificate is null ? null : new SslStream(client.GetStream(), leaveInnerStreamOpen: true);
                if (ssl is not null)
                {
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, _stop.Token).ConfigureAwait(false);
                }

                var stream = (Stream?)ssl ?? client.GetStream();
                var target = _kind == "socks5" ? await SocksAsync(stream).ConfigureAwait(false) : await HttpAsync(stream).ConfigureAwait(false);
                _target.TrySetResult(target);
                await HeadersAsync(stream).ConfigureAwait(false);
                var body = Encoding.ASCII.GetBytes(ExternalProxyFixture.ExitPage);
                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or AuthenticationException or ObjectDisposedException)
            {
                // Клиент отменил пробу либо стенд остановлен.
            }
        }
    }

    private async Task<string> HttpAsync(Stream stream)
    {
        var headers = await HeadersAsync(stream).ConfigureAwait(false);
        var lines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var command = lines[0].Split(' ');
        command[0].ShouldBe("CONNECT");
        var authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            ExternalProxyFixture.Username + ":" + ExternalProxyFixture.Password));
        lines.Single(line => line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim().ShouldBe(authorization);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), _stop.Token).ConfigureAwait(false);
        return command[1];
    }

    private async Task<string> SocksAsync(Stream stream)
    {
        var hello = await ReadAsync(stream, 2).ConfigureAwait(false);
        hello[0].ShouldBe((byte)5);
        (await ReadAsync(stream, hello[1]).ConfigureAwait(false)).ShouldContain((byte)2);
        await stream.WriteAsync(new byte[] { 5, 2 }, _stop.Token).ConfigureAwait(false);
        var auth = await ReadAsync(stream, 2).ConfigureAwait(false);
        auth[0].ShouldBe((byte)1);
        Encoding.UTF8.GetString(await ReadAsync(stream, auth[1]).ConfigureAwait(false)).ShouldBe(ExternalProxyFixture.Username);
        var length = (await ReadAsync(stream, 1).ConfigureAwait(false))[0];
        Encoding.UTF8.GetString(await ReadAsync(stream, length).ConfigureAwait(false)).ShouldBe(ExternalProxyFixture.Password);
        await stream.WriteAsync(new byte[] { 1, 0 }, _stop.Token).ConfigureAwait(false);
        var header = await ReadAsync(stream, 4).ConfigureAwait(false);
        header[1].ShouldBe((byte)1, "контрольная проба использует TCP CONNECT");
        header[3].ShouldBe((byte)3, "неразрешенное имя передано внешнему прокси, а не DNS ОС клиента");
        length = (await ReadAsync(stream, 1).ConfigureAwait(false))[0];
        var host = Encoding.ASCII.GetString(await ReadAsync(stream, length).ConfigureAwait(false));
        var port = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(stream, 2).ConfigureAwait(false));
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, _stop.Token).ConfigureAwait(false);
        return host + ":" + port;
    }

    private async Task<string> HeadersAsync(Stream stream)
    {
        var bytes = new List<byte>();
        while (bytes.Count < 8192)
        {
            bytes.Add((await ReadAsync(stream, 1).ConfigureAwait(false))[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
            {
                return Encoding.ASCII.GetString([.. bytes]);
            }
        }

        throw new IOException("Слишком большой заголовок контрольного запроса");
    }

    private async Task<byte[]> ReadAsync(Stream stream, int count)
    {
        var result = new byte[count];
        await stream.ReadExactlyAsync(result, _stop.Token).ConfigureAwait(false);
        return result;
    }
}
