using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Замеры сетевого инспектора (этап 7) до узла, который назвал пользователь: пинг, трассировка, поиск имени, TCP и TLS.
/// Идут как вся сеть службы — при VPN через туннель — и только читают: на машине ничего не меняют, узел в журнал не
/// пишется. Имя ищется через DNS системы, замер идет к первому адресу (порядок адресов — системы).
/// </summary>
/// <param name="time">Часы замеров.</param>
/// <param name="resolve">Поиск адресов имени; null — DNS системы.</param>
/// <param name="interval">Пауза между эхо пинга; null — секунда.</param>
public sealed class ProbeRunner(TimeProvider time, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeSpan? interval = null)
{
    /// <summary>Сколько эхо в пинге.</summary>
    public const int PingCount = 4;

    /// <summary>Сколько участков проходит трассировка, пока не дойдет.</summary>
    public const int MaxHops = 30;

    /// <summary>Сколько ждать эхо и участок трассировки.</summary>
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Сколько ждать поиск имени, соединение TCP и рукопожатие TLS.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static readonly byte[] Payload = new byte[32];

    /// <summary>
    /// Замер <paramref name="kind"/> до <paramref name="target"/> (уже проверенного <see cref="ProbeTarget.Normalize"/>):
    /// по строке на эхо, участок трассировки или адрес имени; у TCP и TLS — одна строка. Имя не нашлось — одна строка
    /// <see cref="ProbeOutcome.NotFound"/>.
    /// </summary>
    public async IAsyncEnumerable<ProbeStep> RunAsync(ProbeKind kind, string target, int port, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var started = time.GetTimestamp();
        var (addresses, failure) = IPAddress.TryParse(target, out var literal)
            ? ([literal], null)
            : await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            yield return new ProbeStep(1, null, null, failure ?? ProbeOutcome.NotFound);
            yield break;
        }

        var address = addresses[0];
        switch (kind)
        {
            case ProbeKind.Dns:
                var took = time.GetElapsedTime(started);
                for (var i = 0; i < addresses.Count; i++)
                {
                    yield return new ProbeStep(i + 1, addresses[i], took, ProbeOutcome.Ok);
                }

                break;
            case ProbeKind.Ping:
                for (var i = 1; i <= PingCount; i++)
                {
                    yield return await EchoAsync(i, address, null, cancellationToken).ConfigureAwait(false);
                    if (i < PingCount)
                    {
                        await Task.Delay(interval ?? TimeSpan.FromSeconds(1), time, cancellationToken).ConfigureAwait(false);
                    }
                }

                break;
            case ProbeKind.Trace:
                for (var ttl = 1; ttl <= MaxHops; ttl++)
                {
                    var hop = await EchoAsync(ttl, address, ttl, cancellationToken).ConfigureAwait(false);
                    yield return hop;
                    if (hop.Outcome is ProbeOutcome.Ok or ProbeOutcome.Unreachable or ProbeOutcome.Failed)
                    {
                        break;
                    }
                }

                break;
            case ProbeKind.Tcp:
                {
                    var (step, client) = await ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                    client?.Dispose();
                    yield return step;
                    break;
                }

            case ProbeKind.Tls:
                yield return await HandshakeAsync(address, port, literal is null ? target : null, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>Итог эхо по ответу ОС.</summary>
    public static ProbeOutcome Outcome(IPStatus status) => status switch
    {
        IPStatus.Success => ProbeOutcome.Ok,
        IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded => ProbeOutcome.Hop,
        IPStatus.TimedOut => ProbeOutcome.TimedOut,
        IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationUnreachable
            or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.DestinationProhibited
            or IPStatus.BadRoute => ProbeOutcome.Unreachable,
        _ => ProbeOutcome.Failed,
    };

    /// <summary>Итог соединения по ошибке сокета.</summary>
    public static ProbeOutcome Outcome(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => ProbeOutcome.Refused,
        SocketError.TimedOut => ProbeOutcome.TimedOut,
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown => ProbeOutcome.Unreachable,
        SocketError.HostNotFound or SocketError.NoData => ProbeOutcome.NotFound,
        _ => ProbeOutcome.Failed,
    };

    /// <summary>
    /// Что не так с сертификатом: ошибки проверки ОС и состояние цепочки; срок — сравнением с <paramref name="now"/>.
    /// Все в порядке — пусто.
    /// </summary>
    public static IReadOnlyList<TlsProblem> Problems(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chain, X509Certificate2? certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chain);
        var problems = new List<TlsProblem>();
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
        {
            problems.Add(TlsProblem.NameMismatch);
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            problems.Add(TlsProblem.Other);
        }

        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
        {
            foreach (var flag in chain.Where(f => f != X509ChainStatusFlags.NoError))
            {
                problems.Add(flag switch
                {
                    X509ChainStatusFlags.NotTimeValid when certificate is not null && now < certificate.NotBefore.ToUniversalTime() => TlsProblem.NotYetValid,
                    X509ChainStatusFlags.NotTimeValid => TlsProblem.Expired,
                    X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain => TlsProblem.Untrusted,
                    _ => TlsProblem.Other,
                });
            }

            if (problems.Count == 0 || problems.TrueForAll(p => p == TlsProblem.NameMismatch))
            {
                // ОС сказала «ошибка цепочки», не назвав какой.
                problems.Add(TlsProblem.Other);
            }
        }

        return [.. problems.Distinct()];
    }

    /// <summary>«TLS 1.3» по версии протокола.</summary>
    public static string ProtocolName(SslProtocols protocol) => (int)protocol switch
    {
        // Значения SslProtocols: старые версии — только назвать (их имена в .NET помечены устаревшими).
        0x3000 => "TLS 1.3",
        0xC00 => "TLS 1.2",
        0x300 => "TLS 1.1",
        0xC0 => "TLS 1.0",
        _ => protocol.ToString(),
    };

    private async Task<(IReadOnlyList<IPAddress> Addresses, ProbeOutcome? Failure)> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var found = await (resolve ?? Dns.GetHostAddressesAsync)(name, cancellationToken).WaitAsync(Timeout, time, cancellationToken).ConfigureAwait(false);
            return (found, null);
        }
        catch (SocketException e)
        {
            return ([], Outcome(e.SocketErrorCode) is ProbeOutcome.Failed or ProbeOutcome.NotFound ? ProbeOutcome.NotFound : Outcome(e.SocketErrorCode));
        }
        catch (TimeoutException)
        {
            return ([], ProbeOutcome.TimedOut);
        }
    }

    private async Task<ProbeStep> EchoAsync(int index, IPAddress address, int? ttl, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var started = time.GetTimestamp();
        try
        {
            var reply = await ping.SendPingAsync(address, EchoTimeout, Payload, ttl is { } hops ? new PingOptions(hops, dontFragment: true) : null, cancellationToken)
                .ConfigureAwait(false);
            var outcome = Outcome(reply.Status);
            var from = reply.Address is { } replied && !replied.Equals(IPAddress.Any) && !replied.Equals(IPAddress.IPv6Any) ? replied : null;
            TimeSpan? took = outcome is ProbeOutcome.Ok or ProbeOutcome.Hop
                ? reply.RoundtripTime > 0 ? TimeSpan.FromMilliseconds(reply.RoundtripTime) : time.GetElapsedTime(started)
                : null;
            return new ProbeStep(index, outcome == ProbeOutcome.TimedOut ? null : from ?? (outcome == ProbeOutcome.Ok ? address : null), took, outcome);
        }
        catch (PingException)
        {
            return new ProbeStep(index, null, null, ProbeOutcome.Failed);
        }
    }

    private async Task<(ProbeStep Step, TcpClient? Client)> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var started = time.GetTimestamp();
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return (new ProbeStep(1, address, time.GetElapsedTime(started), ProbeOutcome.Ok), client);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            return (new ProbeStep(1, address, null, ProbeOutcome.TimedOut), null);
        }
        catch (SocketException e)
        {
            client.Dispose();
            return (new ProbeStep(1, address, null, Outcome(e.SocketErrorCode)), null);
        }
    }

    private async Task<ProbeStep> HandshakeAsync(IPAddress address, int port, string? name, CancellationToken cancellationToken)
    {
        var (connected, client) = await ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return connected;
        }

        using var tcp = client;
        SslPolicyErrors errors = default;
        X509ChainStatusFlags[] chain = [];
        X509Certificate2? certificate = null;
        var options = new SslClientAuthenticationOptions
        {
            // У адреса без имени SNI нет, имя сертификата сверяется с адресом.
            TargetHost = name ?? address.ToString(),
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, chained, policy) =>
            {
                errors = policy;
                chain = chained?.ChainStatus.Select(s => s.Status).ToArray() ?? [];
                certificate = cert is null ? null : new X509Certificate2(cert);
                return true;
            },
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var started = time.GetTimestamp();
        using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsClientAsync(options, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeStep(1, address, null, ProbeOutcome.TimedOut);
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            return new ProbeStep(1, address, null, ProbeOutcome.Failed);
        }

        using var held = certificate;
        if (held is null)
        {
            return new ProbeStep(1, address, time.GetElapsedTime(started), ProbeOutcome.Failed);
        }

        var names = held.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(a => a.ToString())))
            .ToList();
        var facts = new TlsFacts(
            ProtocolName(tls.SslProtocol),
            tls.NegotiatedCipherSuite.ToString(),
            tls.NegotiatedApplicationProtocol.Protocol.Length > 0 ? tls.NegotiatedApplicationProtocol.ToString() : null,
            held.Subject,
            held.Issuer,
            held.NotBefore.ToUniversalTime(),
            held.NotAfter.ToUniversalTime(),
            names,
            Problems(errors, chain, held, time.GetUtcNow()));
        return new ProbeStep(1, address, time.GetElapsedTime(started), ProbeOutcome.Ok, facts);
    }
}
