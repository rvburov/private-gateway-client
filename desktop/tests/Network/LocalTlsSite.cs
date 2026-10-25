using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Сайт TLS 1.3 на 127.0.0.1 с самоподписанным сертификатом — «маска» (target) для REALITY: сервер
/// REALITY повторяет его рукопожатие. Только рукопожатие, без HTTP.
/// </summary>
internal sealed class LocalTlsSite : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();

    public LocalTlsSite(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Ключ, которым пользуется SslStream на всех ОС, — только из PKCS#12.
        _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Endpoint => "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Сколько рукопожатий TLS 1.3 прошло.</summary>
    public int Handshakes => Volatile.Read(ref _handshakes);

    private int _handshakes;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        _certificate.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        await using var tls = new SslStream(client.GetStream());
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            EnabledSslProtocols = SslProtocols.Tls13,
                            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                        }).ConfigureAwait(false);
                        Interlocked.Increment(ref _handshakes);
                        await tls.ReadAsync(new byte[1]).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or AuthenticationException or ObjectDisposedException)
                    {
                        // Сервер REALITY рвет соединение с маской, когда рукопожатие ему больше не нужно.
                    }
                }
            });
        }
    }
}
