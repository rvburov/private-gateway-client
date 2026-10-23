using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Network;

/// <summary>
/// Шифруется ли трафик до сервера. Подпись «Трафик зашифрован» на главном экране берется
/// отсюда, а не ставится всегда: VLESS без TLS и REALITY передает данные открыто.
/// </summary>
public static class TrafficEncryption
{
    public static bool IsEncrypted(ProxyProtocol protocol, TransportSecurity security) => protocol switch
    {
        // Шифруют сами, независимо от транспорта.
        ProxyProtocol.Vmess or ProxyProtocol.Shadowsocks or ProxyProtocol.WireGuard => true,
        // Работают только поверх TLS (QUIC) — без него не подключатся.
        ProxyProtocol.Trojan or ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic => true,
        // VLESS собственного шифрования не имеет.
        ProxyProtocol.Vless => security != TransportSecurity.None,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Неизвестный протокол"),
    };
}
