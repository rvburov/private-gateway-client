using System.Net;

namespace PrivateGateway.Client.Core.Inspector;

/// <summary>
/// Сеть машины в один момент — глазами сайтов и ОС — для сравнения до и после VPN.
/// </summary>
/// <param name="TakenAt">Когда снято.</param>
/// <param name="Ipv4">Адрес, который видят сайты по IPv4; не измерен — null.</param>
/// <param name="Ipv6">Адрес IPv6, который видят сайты; не измерен или IPv6 нет — null.</param>
/// <param name="Webrtc">Адрес и порт, которые видит сервер STUN (так WebRTC узнает адрес); не ответил — null.</param>
/// <param name="Route">Адаптер, через который ОС отправляет трафик в интернет; неизвестен — null.</param>
/// <param name="Gateway">Шлюз этого адаптера; нет или неизвестен — null.</param>
/// <param name="Dns">DNS-серверы этого адаптера.</param>
public sealed record NetworkFacts(
    DateTimeOffset TakenAt,
    string? Ipv4,
    string? Ipv6,
    IPEndPoint? Webrtc,
    string? Route,
    IPAddress? Gateway,
    IReadOnlyList<IPAddress> Dns);

/// <summary>
/// До и после VPN: «до» служба снимает при включении защиты (пока прямой путь открыт), «после» — по просьбе. Включали
/// защиту, пока служба работает, — есть «до», нет — только «сейчас».
/// </summary>
public sealed record NetworkComparison(NetworkFacts? Before, NetworkFacts Now);
