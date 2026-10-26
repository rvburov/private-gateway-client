using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Core.Lab;

/// <summary>
/// Факты машины для объяснений лаборатории (этап 8): что сейчас на каждом уровне сети — из снимка сети, сокетов и состояния
/// защиты. Чего служба не дала — null: объяснение говорит «не измерено», а не подставляет типичное значение.
/// </summary>
/// <param name="Physical">Адаптер выхода в интернет (не туннель); нет — null.</param>
/// <param name="Tunnel">Туннель защиты: включенный туннельный адаптер, на который ведут маршруты в интернет; нет — null.</param>
/// <param name="TunnelRoutes">Маршруты в интернет через туннель: «0.0.0.0/1», «128.0.0.0/1», «default».</param>
/// <param name="RouteCount">Сколько маршрутов в таблице; снимка нет — null.</param>
/// <param name="Dns">Серверы DNS, которыми пользуется ОС: туннеля, когда он есть, иначе — адаптера выхода.</param>
/// <param name="TcpOpen">Установленные соединения TCP; сокетов нет — null.</param>
/// <param name="TcpListening">Сокеты TCP, ждущие входящих; сокетов нет — null.</param>
/// <param name="Udp">Сокеты UDP; сокетов нет — null.</param>
/// <param name="SeenIp">Адрес, который видят сайты (служба мерила); нет — null.</param>
/// <param name="RealIp">Реальный адрес от провайдера; нет — null.</param>
public sealed record MachineFacts(
    NetworkCard? Physical,
    NetworkCard? Tunnel,
    IReadOnlyList<string> TunnelRoutes,
    int? RouteCount,
    IReadOnlyList<IPAddress> Dns,
    int? TcpOpen,
    int? TcpListening,
    int? Udp,
    string? SeenIp,
    string? RealIp)
{
    // Маршруты «в интернет»: по умолчанию и половины адресного пространства, которыми туннели перекрывают маршрут по умолчанию.
    private static readonly HashSet<string> Internet = new(StringComparer.Ordinal) { "default", "0.0.0.0/0", "::/0", "0.0.0.0/1", "128.0.0.0/1", "0/1", "128.0/1", "::/1", "8000::/1" };

    public static MachineFacts From(NetworkSnapshot? snapshot, ConnectionStatus status, IReadOnlyList<NetworkConnection>? connections)
    {
        ArgumentNullException.ThrowIfNull(status);
        var physical = PathBuilder.Physical(snapshot);
        var routes = snapshot?.Routes ?? [];
        var tunnel = snapshot?.Cards.FirstOrDefault(c => c is { IsUp: true, Kind: AdapterKind.Tunnel }
            && routes.Any(r => r.Interface == c.Name && Internet.Contains(r.Destination)));
        var tunnelRoutes = tunnel is null ? [] : routes.Where(r => r.Interface == tunnel.Name && Internet.Contains(r.Destination)).Select(r => r.Destination).ToList();
        var dns = tunnel is { DnsServers.Count: > 0 } ? tunnel.DnsServers : physical?.DnsServers ?? [];
        var protectedNow = status is { State: ConnectionState.Connected, IsPaused: false };
        return new MachineFacts(
            physical,
            tunnel,
            tunnelRoutes,
            snapshot?.Routes.Count,
            dns,
            connections?.Count(c => c.Socket is { Protocol: SocketProtocol.Tcp, State: SocketState.Established }),
            connections?.Count(c => c.Socket is { Protocol: SocketProtocol.Tcp, State: SocketState.Listen }),
            connections?.Count(c => c.Socket.Protocol == SocketProtocol.Udp),
            protectedNow ? status.Exit.Ip : status.Exit.DirectIp ?? status.Exit.Ip,
            status.Exit.DirectIp);
    }

    /// <summary>IPv4-адрес адаптера с префиксом: «192.0.2.23/24»; нет — null.</summary>
    public static NetworkAddress? Ipv4(NetworkCard? card) => card?.Addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Глобальный IPv6 адаптера (не локальный канала); нет — null.</summary>
    public static NetworkAddress? Ipv6(NetworkCard? card) =>
        card?.Addresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal && !a.Address.Equals(IPAddress.IPv6Loopback));
}
