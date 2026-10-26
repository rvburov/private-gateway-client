using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Замеры инспектора до узла (этап 7): пинг и путь до петли, поиск имени, TCP и TLS на своих серверах 127.0.0.1 с
/// самоподписанным сертификатом, перевод ответов ОС в итог и замер через IPC. Из машины ничего не уходит.
/// </summary>
public sealed class ProbeRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ping_of_the_loopback_answers_every_echo()
    {
        var steps = await Run(ProbeKind.Ping, "127.0.0.1");

        steps.Select(s => s.Index).ShouldBe([1, 2, 3, 4]);
        steps.ShouldAllBe(s => s.Outcome == ProbeOutcome.Ok && s.Time != null && Equals(s.Address, IPAddress.Loopback));
    }

    [Fact]
    public async Task Route_to_the_loopback_is_one_hop_and_stops_there()
    {
        var step = (await Run(ProbeKind.Trace, "127.0.0.1")).ShouldHaveSingleItem("дошли — дальше не идем");

        (step.Index, step.Outcome, step.Address).ShouldBe((1, ProbeOutcome.Ok, IPAddress.Loopback));
    }

    [Fact]
    public async Task Name_lookup_gives_every_address_and_a_missing_name_is_not_found()
    {
        var found = await Run(ProbeKind.Dns, "example.com", resolve: (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::10") }));
        found.Select(s => (s.Index, s.Address!.ToString(), s.Outcome)).ShouldBe([(1, "203.0.113.10", ProbeOutcome.Ok), (2, "2001:db8::10", ProbeOutcome.Ok)]);
        found.ShouldAllBe(s => s.Time != null);

        var missing = await Run(ProbeKind.Ping, "nxdomain.example", resolve: (_, _) => throw new SocketException((int)SocketError.HostNotFound));
        missing.ShouldHaveSingleItem().Outcome.ShouldBe(ProbeOutcome.NotFound, "имени нет — ни одного эхо");
        (await Run(ProbeKind.Dns, "example.com", resolve: (_, _) => Task.FromResult(Array.Empty<IPAddress>()))).ShouldHaveSingleItem().Outcome
            .ShouldBe(ProbeOutcome.NotFound, "у имени нет адресов");

        (await Run(ProbeKind.Dns, "localhost")).ShouldContain(s => s.Address != null && IPAddress.IsLoopback(s.Address), "DNS системы");
    }

    [Fact]
    public async Task Tcp_tells_an_open_port_from_a_closed_one()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var open = (await Run(ProbeKind.Tcp, "127.0.0.1", port)).ShouldHaveSingleItem();
            (open.Outcome, open.Address).ShouldBe((ProbeOutcome.Ok, IPAddress.Loopback));
            open.Time.ShouldNotBeNull();
        }
        finally
        {
            listener.Stop();
        }

        (await Run(ProbeKind.Tcp, "127.0.0.1", XrayKit.FreePort())).ShouldHaveSingleItem().Outcome.ShouldBe(ProbeOutcome.Refused);
    }

    [Fact]
    public async Task Tls_shows_the_handshake_and_what_is_wrong_with_the_certificate()
    {
        var now = DateTimeOffset.UtcNow;
        using var site = TlsSite.Start(SelfSigned("example.com", now.AddDays(-1), now.AddDays(30)));
        var byName = (await Run(ProbeKind.Tls, "example.com", site.Port, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }))).ShouldHaveSingleItem();

        byName.Outcome.ShouldBe(ProbeOutcome.Ok);
        var tls = byName.Tls.ShouldNotBeNull();
        tls.Protocol.ShouldBeOneOf("TLS 1.2", "TLS 1.3");
        tls.Cipher.ShouldNotBeNullOrEmpty();
        tls.Alpn.ShouldBe("h2", "сервер согласился на h2");
        tls.Subject.ShouldBe("CN=example.com");
        tls.Names.ShouldBe(["example.com"]);
        tls.NotAfter.ShouldBe(now.AddDays(30), TimeSpan.FromSeconds(1));
        tls.Problems.ShouldBe([TlsProblem.Untrusted], "самоподписанный, а имя — то");
        site.Names.ShouldBe(["example.com"], "имя узла ушло в SNI");

        var byAddress = (await Run(ProbeKind.Tls, "127.0.0.1", site.Port)).ShouldHaveSingleItem();
        byAddress.Tls.ShouldNotBeNull().Problems.ShouldBe([TlsProblem.NameMismatch, TlsProblem.Untrusted], ignoreOrder: true);

        using var old = TlsSite.Start(SelfSigned("example.com", now.AddDays(-60), now.AddDays(-1)));
        (await Run(ProbeKind.Tls, "example.com", old.Port, (_, _) => Task.FromResult(new[] { IPAddress.Loopback })))
            .ShouldHaveSingleItem().Tls.ShouldNotBeNull().Problems.ShouldContain(TlsProblem.Expired);

        (await Run(ProbeKind.Tls, "127.0.0.1", XrayKit.FreePort())).ShouldHaveSingleItem().Outcome
            .ShouldBe(ProbeOutcome.Refused, "порт закрыт — до рукопожатия не дошло");
    }

    [Fact]
    public async Task Tls_to_a_port_without_tls_fails_and_does_not_hang()
    {
        // Сервер сразу пишет не TLS и закрывает соединение.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await client.GetStream().WriteAsync("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray(), Ct);
        }, Ct);
        try
        {
            var step = (await Run(ProbeKind.Tls, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port)).ShouldHaveSingleItem();

            step.Outcome.ShouldBe(ProbeOutcome.Failed);
            step.Tls.ShouldBeNull();
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(IPStatus.Success, ProbeOutcome.Ok)]
    [InlineData(IPStatus.TtlExpired, ProbeOutcome.Hop)]
    [InlineData(IPStatus.TimeExceeded, ProbeOutcome.Hop)]
    [InlineData(IPStatus.TimedOut, ProbeOutcome.TimedOut)]
    [InlineData(IPStatus.DestinationHostUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.DestinationNetworkUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.DestinationProhibited, ProbeOutcome.Unreachable)]
    [InlineData(IPStatus.PacketTooBig, ProbeOutcome.Failed)]
    public void Echo_reply_of_the_os_becomes_an_outcome(IPStatus status, ProbeOutcome outcome) => ProbeRunner.Outcome(status).ShouldBe(outcome);

    [Theory]
    [InlineData(SocketError.ConnectionRefused, ProbeOutcome.Refused)]
    [InlineData(SocketError.TimedOut, ProbeOutcome.TimedOut)]
    [InlineData(SocketError.NetworkUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(SocketError.HostUnreachable, ProbeOutcome.Unreachable)]
    [InlineData(SocketError.HostNotFound, ProbeOutcome.NotFound)]
    [InlineData(SocketError.AccessDenied, ProbeOutcome.Failed)]
    public void Socket_error_becomes_an_outcome(SocketError error, ProbeOutcome outcome) => ProbeRunner.Outcome(error).ShouldBe(outcome);

    [Fact]
    public void Certificate_problems_are_named_from_the_policy_and_the_chain()
    {
        var now = DateTimeOffset.UtcNow;
        using var future = SelfSigned("example.com", now.AddDays(1), now.AddDays(30));
        ProbeRunner.Problems(SslPolicyErrors.None, [], future, now).ShouldBeEmpty();
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.NotTimeValid], future, now).ShouldBe([TlsProblem.NotYetValid]);
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.NotTimeValid], future, now.AddDays(40)).ShouldBe([TlsProblem.Expired]);
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            [X509ChainStatusFlags.UntrustedRoot, X509ChainStatusFlags.PartialChain, X509ChainStatusFlags.NoError], future, now)
            .ShouldBe([TlsProblem.NameMismatch, TlsProblem.Untrusted], "одна проблема — один раз");
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateChainErrors, [X509ChainStatusFlags.Revoked], future, now).ShouldBe([TlsProblem.Other]);
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateChainErrors, [], future, now).ShouldBe([TlsProblem.Other], "ошибка цепочки без подробностей");
        ProbeRunner.Problems(SslPolicyErrors.RemoteCertificateNotAvailable, [], null, now).ShouldBe([TlsProblem.Other]);

        ProbeRunner.ProtocolName(SslProtocols.Tls13).ShouldBe("TLS 1.3");
        ProbeRunner.ProtocolName(SslProtocols.Tls12).ShouldBe("TLS 1.2");
        ProbeRunner.ProtocolName((SslProtocols)0x300).ShouldBe("TLS 1.1");
    }

    [Fact]
    public void Probe_step_goes_through_the_contract_and_comes_back_the_same()
    {
        var tls = new TlsFacts("TLS 1.3", "TLS_AES_128_GCM_SHA256", "h2", "CN=example.com", "CN=Example CA",
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ["example.com", "www.example.com"], [TlsProblem.NameMismatch, TlsProblem.Untrusted]);
        var step = new ProbeStep(1, IPAddress.Parse("203.0.113.10"), TimeSpan.FromMilliseconds(24.5), ProbeOutcome.Ok, tls);

        var back = step.ToContract().ToCore();

        (back with { Tls = null }).ShouldBe(step with { Tls = null });
        (back.Tls! with { Names = [], Problems = [] }).ShouldBe(tls with { Names = [], Problems = [] });
        back.Tls!.Names.ShouldBe(tls.Names);
        back.Tls.Problems.ShouldBe(tls.Problems);
        new ProbeStep(3, null, null, ProbeOutcome.TimedOut).ToContract().ToCore().ShouldBe(new ProbeStep(3, null, null, ProbeOutcome.TimedOut));
        foreach (var outcome in Enum.GetValues<ProbeOutcome>())
        {
            outcome.ToContract().ToCore().ShouldBe(outcome);
        }

        foreach (var kind in Enum.GetValues<ProbeKind>())
        {
            kind.ToContract().ToCore().ShouldBe(kind);
        }

        foreach (var problem in Enum.GetValues<TlsProblem>())
        {
            problem.ToContract().ToCore().ShouldBe(problem);
        }
    }

    [Fact]
    public async Task Service_runs_a_probe_through_ipc_and_refuses_what_is_not_a_host()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var service = await RunningService.StartAsync();
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);
        try
        {
            var steps = new List<ProbeStep>();
            await foreach (var step in client.ProbeAsync(ProbeKind.Tcp, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Ct))
            {
                steps.Add(step);
            }

            steps.ShouldHaveSingleItem().Outcome.ShouldBe(ProbeOutcome.Ok);
        }
        finally
        {
            listener.Stop();
        }

        foreach (var request in new[]
        {
            new V1.ProbeRequest { Kind = V1.ProbeKind.Ping, Target = "example.com; reboot" },
            new V1.ProbeRequest { Kind = V1.ProbeKind.Ping, Target = "https://example.com/" },
            new V1.ProbeRequest { Kind = V1.ProbeKind.Tcp, Target = "example.com", Port = 0 },
            new V1.ProbeRequest { Kind = V1.ProbeKind.Tls, Target = "example.com", Port = 70000 },
            new V1.ProbeRequest { Target = "example.com" },
        })
        {
            var refused = await Should.ThrowAsync<RpcException>(async () =>
            {
                using var call = service.Client.RunProbe(request, cancellationToken: Ct);
                await call.ResponseStream.MoveNext(Ct);
            });
            refused.StatusCode.ShouldBe(StatusCode.InvalidArgument, request.ToString());
        }
    }

    private static async Task<List<ProbeStep>> Run(ProbeKind kind, string target, int port = 0, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var runner = new ProbeRunner(TimeProvider.System, resolve, TimeSpan.FromMilliseconds(10));
        var steps = new List<ProbeStep>();
        await foreach (var step in runner.RunAsync(kind, target, port, Ct))
        {
            steps.Add(step);
        }

        return steps;
    }

    // Самоподписанный сертификат с именем в SAN; через PKCS#12 — иначе ключ эфемерный, и Windows и macOS не дают им
    // пользоваться серверу TLS.
    private static X509Certificate2 SelfSigned(string name, DateTimeOffset from, DateTimeOffset until)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        using var made = request.CreateSelfSigned(from, until);
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>Свой сервер TLS на 127.0.0.1: рукопожатие с h2 и закрытие; запоминает, какие имена пришли в SNI.</summary>
    private sealed class TlsSite : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _names = [];

        private TlsSite(X509Certificate2 certificate) => _certificate = certificate;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Names
        {
            get
            {
                lock (_names)
                {
                    return [.. _names];
                }
            }
        }

        public static TlsSite Start(X509Certificate2 certificate)
        {
            var site = new TlsSite(certificate);
            site._listener.Start();
            _ = Task.Run(site.ServeAsync);
            return site;
        }

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
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var tls = new SslStream(client.GetStream()))
                    {
                        try
                        {
                            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificateSelectionCallback = (_, name) =>
                                {
                                    if (!string.IsNullOrEmpty(name))
                                    {
                                        lock (_names)
                                        {
                                            _names.Add(name);
                                        }
                                    }

                                    return _certificate;
                                },
                                ApplicationProtocols = [SslApplicationProtocol.Http2],
                            });
                        }
                        catch (Exception e) when (e is AuthenticationException or IOException)
                        {
                            // Клиент оборвал рукопожатие.
                        }
                    }
                });
            }
        }
    }
}
