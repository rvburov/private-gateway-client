using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Сведения о сервере для подписей интерфейса.</summary>
public static class ServerFacts
{
    /// <summary>Шифруется ли трафик до сервера — от этого зависит подпись «Трафик зашифрован».</summary>
    public static bool IsEncrypted(ServerProfile server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return TrafficEncryption.IsEncrypted(server.Protocol, server.Security);
    }

    /// <summary>Имя протокола так, как его пишут сами протоколы.</summary>
    public static string ProtocolName(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Vless => "VLESS",
        ProxyProtocol.Vmess => "VMess",
        ProxyProtocol.Trojan => "Trojan",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        ProxyProtocol.Hysteria2 => "Hysteria2",
        ProxyProtocol.Tuic => "TUIC",
        ProxyProtocol.WireGuard => "WireGuard",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    /// <summary>Имя защиты транспорта; без защиты — null (в подписи не пишется).</summary>
    public static string? SecurityName(TransportSecurity security) => security switch
    {
        TransportSecurity.Reality => "REALITY",
        TransportSecurity.Tls => "TLS",
        _ => null,
    };
}
