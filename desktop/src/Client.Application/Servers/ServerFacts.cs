using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Сведения о сервере для подписей интерфейса.</summary>
public static class ServerFacts
{
    /// <summary>
    /// Сервер, через который идет защита: его называет служба в состоянии. Защита выключена или сервера нет в списке —
    /// выбранный. Выбранный и рабочий расходятся после восстановления из копии, при окне другого пользователя на той же
    /// службе, после подключения при загрузке — на главной и в трее показывается рабочий.
    /// </summary>
    public static ServerProfile? InUse(IServerCatalog catalog, ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(status);
        return status.State != ConnectionState.Off && status.ServerId is { } id && catalog.Servers.FirstOrDefault(s => s.Id == id) is { } working
            ? working
            : catalog.Selected;
    }

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

    /// <summary>Имя транспорта для подписи; обычный TCP, UDP и QUIC — null (не пишутся).</summary>
    public static string? TransportName(string transport) => transport.ToUpperInvariant() switch
    {
        "" or "TCP" or "RAW" or "UDP" or "QUIC" => null,
        "WS" or "WEBSOCKET" => "WS",
        "GRPC" => "gRPC",
        "XHTTP" or "SPLITHTTP" => "XHTTP",
        "HTTPUPGRADE" => "HTTPUpgrade",
        "H2" or "HTTP" => "HTTP/2",
        "KCP" or "MKCP" => "mKCP",
        var other => other,
    };

    /// <summary>
    /// Каким ядром служба соединяется с сервером: Xray-core — основное (и WireGuard), Hysteria2 и TUIC — ретранслятор
    /// sing-box за Xray (решение 452).
    /// </summary>
    public static string CoreName(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic => "sing-box",
        _ => "Xray-core",
    };

    /// <summary>
    /// Короткая подпись протокола, как в списке серверов: «VLESS · REALITY», «VLESS · XHTTP»,
    /// «Trojan · WS», «Hysteria2». Второе слово — то, что отличает сервер: REALITY, иначе
    /// транспорт, иначе TLS у протоколов, которые бывают и без него.
    /// </summary>
    public static string Summary(ServerProfile server)
    {
        ArgumentNullException.ThrowIfNull(server);
        // TUIC поддерживается только пятой версии (ядро sing-box) — версию пишем, как в макете.
        var name = server.Protocol == ProxyProtocol.Tuic ? "TUIC v5" : ProtocolName(server.Protocol);
        var detail = server.Security == TransportSecurity.Reality
            ? SecurityName(server.Security)
            : TransportName(server.Transport)
              ?? (server.Security == TransportSecurity.Tls && server.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess
                  ? SecurityName(server.Security)
                  : null);
        return detail is null ? name : name + " · " + detail;
    }
}
