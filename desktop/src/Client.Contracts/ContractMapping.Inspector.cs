using System.Net;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Core.Inspector;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>Сетевой инспектор: снимок сети машины, сведения об адресе и замеры до узла — в контракт и обратно.</summary>
public static partial class ContractMapping
{
    public static V1.AdapterKind ToContract(this AdapterKind kind) => kind switch
    {
        AdapterKind.Ethernet => V1.AdapterKind.Ethernet,
        AdapterKind.Wifi => V1.AdapterKind.Wifi,
        AdapterKind.Cellular => V1.AdapterKind.Cellular,
        AdapterKind.Tunnel => V1.AdapterKind.Tunnel,
        AdapterKind.Loopback => V1.AdapterKind.Loopback,
        AdapterKind.Other => V1.AdapterKind.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static AdapterKind ToCore(this V1.AdapterKind kind) => kind switch
    {
        V1.AdapterKind.Ethernet => AdapterKind.Ethernet,
        V1.AdapterKind.Wifi => AdapterKind.Wifi,
        V1.AdapterKind.Cellular => AdapterKind.Cellular,
        V1.AdapterKind.Tunnel => AdapterKind.Tunnel,
        V1.AdapterKind.Loopback => AdapterKind.Loopback,
        V1.AdapterKind.Other => AdapterKind.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Вид адаптера не задан"),
    };

    public static V1.NetworkSnapshot ToContract(this NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reply = new V1.NetworkSnapshot { TakenAt = Timestamp.FromDateTimeOffset(snapshot.TakenAt) };
        foreach (var card in snapshot.Cards)
        {
            var item = new V1.NetworkCard
            {
                Name = card.Name,
                Description = card.Description,
                Kind = card.Kind.ToContract(),
                Up = card.IsUp,
                Mac = card.Mac ?? string.Empty,
                Mtu = card.Mtu ?? 0,
                SpeedBps = card.SpeedBitsPerSecond ?? 0,
                Dhcp = card.Dhcp is not null,
                DhcpServer = card.Dhcp?.Server?.ToString() ?? string.Empty,
                DhcpRemaining = card.Dhcp?.Remaining is { } remaining ? Duration.FromTimeSpan(remaining) : null,
            };
            item.Addresses.AddRange(card.Addresses.Select(a => new V1.NetworkAddress { Address = a.Address.ToString(), PrefixLength = a.PrefixLength }));
            item.Gateways.AddRange(card.Gateways.Select(g => g.ToString()));
            item.DnsServers.AddRange(card.DnsServers.Select(d => d.ToString()));
            reply.Cards.Add(item);
        }

        reply.Routes.AddRange(snapshot.Routes.Select(r => new V1.NetworkRoute
        {
            Destination = r.Destination,
            Gateway = r.Gateway?.ToString() ?? string.Empty,
            Interface = r.Interface,
            Metric = r.Metric ?? 0,
        }));
        return reply;
    }

    /// <summary>Снимок от службы; адрес, который не разобрать, пропускается — остальное остается.</summary>
    public static NetworkSnapshot ToCore(this V1.NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var cards = snapshot.Cards.Select(c => new NetworkCard(
            c.Name,
            c.Description,
            c.Kind.ToCore(),
            c.Up,
            c.Mac.Length > 0 ? c.Mac : null,
            c.Mtu > 0 ? c.Mtu : null,
            c.SpeedBps > 0 ? c.SpeedBps : null,
            [.. c.Addresses.Where(a => IPAddress.TryParse(a.Address, out _)).Select(a => new NetworkAddress(IPAddress.Parse(a.Address), a.PrefixLength))],
            Addresses(c.Gateways),
            Addresses(c.DnsServers),
            c.Dhcp ? new DhcpLease(IPAddress.TryParse(c.DhcpServer, out var server) ? server : null, c.DhcpRemaining?.ToTimeSpan()) : null)).ToList();
        var routes = snapshot.Routes.Select(r => new NetworkRoute(
            r.Destination, IPAddress.TryParse(r.Gateway, out var gateway) ? gateway : null, r.Interface, r.Metric > 0 ? r.Metric : null)).ToList();
        return new NetworkSnapshot(snapshot.TakenAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue, cards, routes);
    }

    public static V1.AddressInfo ToContract(this AddressInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new V1.AddressInfo
        {
            Address = info.Address.ToString(),
            Asn = info.Asn ?? 0,
            Holder = info.Holder ?? string.Empty,
            Prefix = info.Prefix ?? string.Empty,
            Country = info.Country ?? string.Empty,
            City = info.City ?? string.Empty,
            Failure = info.Failure switch
            {
                null => V1.AddressFailure.Unspecified,
                AddressFailure.Private => V1.AddressFailure.Private,
                AddressFailure.Unreachable => V1.AddressFailure.Unreachable,
                AddressFailure.NotFound => V1.AddressFailure.NotFound,
                _ => throw new ArgumentOutOfRangeException(nameof(info), info.Failure, null),
            },
        };
    }

    /// <summary>Сведения от службы; адрес не разобрать — <see cref="ArgumentException"/> (ответ не по контракту).</summary>
    public static AddressInfo ToCore(this V1.AddressInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        static string? Text(string value) => value.Length > 0 ? value : null;
        return new AddressInfo(
            IPAddress.TryParse(info.Address, out var address) ? address : throw new ArgumentException("Адрес не разобрать: " + info.Address, nameof(info)),
            info.Asn > 0 ? info.Asn : null,
            Text(info.Holder),
            Text(info.Prefix),
            Text(info.Country),
            Text(info.City),
            info.Failure switch
            {
                V1.AddressFailure.Unspecified => null,
                V1.AddressFailure.Private => AddressFailure.Private,
                V1.AddressFailure.Unreachable => AddressFailure.Unreachable,
                V1.AddressFailure.NotFound => AddressFailure.NotFound,
                _ => AddressFailure.Unreachable,
            });
    }

    public static V1.ProbeKind ToContract(this ProbeKind kind) => kind switch
    {
        ProbeKind.Ping => V1.ProbeKind.Ping,
        ProbeKind.Trace => V1.ProbeKind.Trace,
        ProbeKind.Dns => V1.ProbeKind.Dns,
        ProbeKind.Tcp => V1.ProbeKind.Tcp,
        ProbeKind.Tls => V1.ProbeKind.Tls,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Замер из просьбы окна; не задан — <see cref="ArgumentOutOfRangeException"/> (просьба не по контракту).</summary>
    public static ProbeKind ToCore(this V1.ProbeKind kind) => kind switch
    {
        V1.ProbeKind.Ping => ProbeKind.Ping,
        V1.ProbeKind.Trace => ProbeKind.Trace,
        V1.ProbeKind.Dns => ProbeKind.Dns,
        V1.ProbeKind.Tcp => ProbeKind.Tcp,
        V1.ProbeKind.Tls => ProbeKind.Tls,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static V1.ProbeOutcome ToContract(this ProbeOutcome outcome) => outcome switch
    {
        ProbeOutcome.Ok => V1.ProbeOutcome.Ok,
        ProbeOutcome.Hop => V1.ProbeOutcome.Hop,
        ProbeOutcome.TimedOut => V1.ProbeOutcome.TimedOut,
        ProbeOutcome.Refused => V1.ProbeOutcome.Refused,
        ProbeOutcome.Unreachable => V1.ProbeOutcome.Unreachable,
        ProbeOutcome.NotFound => V1.ProbeOutcome.NotFound,
        ProbeOutcome.Failed => V1.ProbeOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    /// <summary>Итог от службы; незнакомый (служба новее) — «не удалось».</summary>
    public static ProbeOutcome ToCore(this V1.ProbeOutcome outcome) => outcome switch
    {
        V1.ProbeOutcome.Ok => ProbeOutcome.Ok,
        V1.ProbeOutcome.Hop => ProbeOutcome.Hop,
        V1.ProbeOutcome.TimedOut => ProbeOutcome.TimedOut,
        V1.ProbeOutcome.Refused => ProbeOutcome.Refused,
        V1.ProbeOutcome.Unreachable => ProbeOutcome.Unreachable,
        V1.ProbeOutcome.NotFound => ProbeOutcome.NotFound,
        _ => ProbeOutcome.Failed,
    };

    public static V1.TlsProblem ToContract(this TlsProblem problem) => problem switch
    {
        TlsProblem.NameMismatch => V1.TlsProblem.NameMismatch,
        TlsProblem.Expired => V1.TlsProblem.Expired,
        TlsProblem.NotYetValid => V1.TlsProblem.NotYetValid,
        TlsProblem.Untrusted => V1.TlsProblem.Untrusted,
        TlsProblem.Other => V1.TlsProblem.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    /// <summary>Что не так с сертификатом; незнакомое (служба новее) — «другое».</summary>
    public static TlsProblem ToCore(this V1.TlsProblem problem) => problem switch
    {
        V1.TlsProblem.NameMismatch => TlsProblem.NameMismatch,
        V1.TlsProblem.Expired => TlsProblem.Expired,
        V1.TlsProblem.NotYetValid => TlsProblem.NotYetValid,
        V1.TlsProblem.Untrusted => TlsProblem.Untrusted,
        _ => TlsProblem.Other,
    };

    public static V1.ProbeStep ToContract(this ProbeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var contract = new V1.ProbeStep
        {
            Index = step.Index,
            Address = step.Address?.ToString() ?? string.Empty,
            Time = step.Time is { } time ? Duration.FromTimeSpan(time) : null,
            Outcome = step.Outcome.ToContract(),
        };
        if (step.Tls is { } tls)
        {
            contract.Tls = new V1.TlsFacts
            {
                Protocol = tls.Protocol,
                Cipher = tls.Cipher,
                Alpn = tls.Alpn ?? string.Empty,
                Subject = tls.Subject,
                Issuer = tls.Issuer,
                NotBefore = Timestamp.FromDateTimeOffset(tls.NotBefore),
                NotAfter = Timestamp.FromDateTimeOffset(tls.NotAfter),
            };
            contract.Tls.Names.AddRange(tls.Names);
            contract.Tls.Problems.AddRange(tls.Problems.Select(p => p.ToContract()));
        }

        return contract;
    }

    public static ProbeStep ToCore(this V1.ProbeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return new ProbeStep(
            step.Index,
            IPAddress.TryParse(step.Address, out var address) ? address : null,
            step.Time?.ToTimeSpan(),
            step.Outcome.ToCore(),
            step.Tls is { } tls
                ? new TlsFacts(tls.Protocol, tls.Cipher, tls.Alpn.Length > 0 ? tls.Alpn : null, tls.Subject, tls.Issuer,
                    tls.NotBefore?.ToDateTimeOffset() ?? DateTimeOffset.MinValue, tls.NotAfter?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    [.. tls.Names], [.. tls.Problems.Select(p => p.ToCore())])
                : null);
    }

    private static List<IPAddress> Addresses(IEnumerable<string> values) =>
        [.. values.Select(v => IPAddress.TryParse(v, out var address) ? address : null).OfType<IPAddress>()];
}
