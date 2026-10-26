using System.Net;
using Google.Protobuf.WellKnownTypes;
using PrivateGateway.Client.Core.Inspector;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>Сетевой инспектор: снимок сети машины в контракт и обратно.</summary>
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

    private static List<IPAddress> Addresses(IEnumerable<string> values) =>
        [.. values.Select(v => IPAddress.TryParse(v, out var address) ? address : null).OfType<IPAddress>()];
}
