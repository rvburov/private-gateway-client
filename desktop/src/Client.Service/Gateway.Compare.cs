using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Сравнение до и после VPN (сетевой инспектор, этап 7): «до» снимается при включении защиты — маршрут и DNS ОС сразу,
/// адреса для сайтов и WebRTC — вместе с замером реального IP, пока прямой путь открыт; «сейчас» — по просьбе окна теми
/// же замерами, что идут как трафик программ.
/// </summary>
public sealed partial class Gateway
{
    // Снимок «до» последнего включения защиты; не включали, пока служба работает, — null.
    private NetworkFacts? _before;

    // Адрес, который видел сервер STUN напрямую, вместе с реальным IP; сеть сменилась — забыт.
    private IPEndPoint? _directWebrtc;

    /// <summary>До и после VPN: «до» — снятое при включении защиты, «сейчас» — замер теперь.</summary>
    public async Task<NetworkComparison> CompareNetworkAsync(CancellationToken cancellationToken = default)
    {
        var status = Status;
        var link = _probe;
        var proxy = status is { State: ConnectionState.Connected, Mode: ConnectionMode.Proxy, IsPaused: false } && link is not null;
        var exit = status.State == ConnectionState.Connected && !status.IsPaused;

        // IPv6 для сайтов — как у программ: в режиме прокси — через ядро, иначе — обычным путем службы.
        var ip6 = _options.Trace6 is { } trace6
            ? (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(proxy
                ? new ServerProbe(link!.Port, new NetworkCredential(link.User, link.Password), trace6, Timing.ProbeTimeout)
                : new ServerProbe(0, null, trace6, Timing.ProbeTimeout), cancellationToken)
            : Task.FromResult<string?>(null);
        var webrtc = WebrtcAsync(cancellationToken);
        var ipv4 = exit ? status.Exit.Ip : status.Exit.DirectIp ?? _directIp;
        var seen6 = await ip6.ConfigureAwait(false);
        var now = Local(ipv4, IPAddress.TryParse(seen6, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6.ToString() : null,
            await webrtc.ConfigureAwait(false));
        return new NetworkComparison(Volatile.Read(ref _before), now);
    }

    /// <summary>
    /// Адаптер, через который ОС отправит трафик в интернет: сокет UDP «соединяется» с публичным адресом (пакетов при этом
    /// нет), его свой адрес — адрес нужного адаптера. Так учитываются и правила маршрутизации туннеля. Нет сети — null.
    /// </summary>
    public static IPAddress? EgressAddress(IPAddress target)
    {
        ArgumentNullException.ThrowIfNull(target);
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 53));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>Сведения ОС и замеры адресов в одну запись: адаптер выхода в интернет — по адресу из <see cref="EgressAddress"/>.</summary>
    public static NetworkFacts Facts(DateTimeOffset at, NetworkSnapshot snapshot, IPAddress? egress, string? ipv4, string? ipv6, IPEndPoint? webrtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var card = egress is null ? null : snapshot.Cards.FirstOrDefault(c => c.Addresses.Any(a => a.Address.Equals(egress)));
        return new NetworkFacts(at, ipv4, ipv6, webrtc, card?.Name, card is { Gateways.Count: > 0 } ? card.Gateways[0] : null, card?.DnsServers ?? []);
    }

    // Снимок «до» при включении защиты: маршрут и DNS — сразу, адреса — то, что известно о прямом пути.
    private void RememberBefore(NetworkSnapshot snapshot, IPAddress? egress) =>
        Volatile.Write(ref _before, Facts(_time.GetUtcNow(), snapshot, egress, _directIp, _directIp6, _directWebrtc));

    private NetworkFacts Local(string? ipv4, string? ipv6, IPEndPoint? webrtc) =>
        Facts(_time.GetUtcNow(), InspectNetwork(), EgressAddress(Probe4), ipv4, ipv6, webrtc);

    // Адрес STUN обычным путем службы — как у WebRTC браузера (UDP прокси не переносит).
    private Task<IPEndPoint?> WebrtcAsync(CancellationToken cancellationToken) =>
        _options.Webrtc is { } webrtc ? webrtc(cancellationToken) : Task.FromResult<IPEndPoint?>(null);

    // Публичный адрес для выбора адаптера выхода: соединение UDP без пакетов.
    private static readonly IPAddress Probe4 = IPAddress.Parse("1.1.1.1");
}
