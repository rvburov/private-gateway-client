using CoreState = PrivateGateway.Client.Core.ConnectionState;
using CoreProtocol = PrivateGateway.Client.Core.ProxyProtocol;
using CoreSecurity = PrivateGateway.Client.Core.TransportSecurity;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Перевод перечислений ядра программы в контракт IPC и обратно. Значение UNSPECIFIED из
/// контракта — ошибка отправителя: служба такой запрос отклоняет, а не угадывает.
/// </summary>
public static class ContractMapping
{
    /// <summary>Текущая версия контракта, ее служба отдает в ServiceInfo.api_version.</summary>
    public const string ApiVersion = "1";

    public static V1.ConnectionState ToContract(this CoreState state) => state switch
    {
        CoreState.Off => V1.ConnectionState.Off,
        CoreState.Connecting => V1.ConnectionState.Connecting,
        CoreState.Connected => V1.ConnectionState.Connected,
        CoreState.Reconnecting => V1.ConnectionState.Reconnecting,
        CoreState.Blocked => V1.ConnectionState.Blocked,
        CoreState.Error => V1.ConnectionState.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    public static CoreState ToCore(this V1.ConnectionState state) => state switch
    {
        V1.ConnectionState.Off => CoreState.Off,
        V1.ConnectionState.Connecting => CoreState.Connecting,
        V1.ConnectionState.Connected => CoreState.Connected,
        V1.ConnectionState.Reconnecting => CoreState.Reconnecting,
        V1.ConnectionState.Blocked => CoreState.Blocked,
        V1.ConnectionState.Error => CoreState.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Состояние не задано"),
    };

    public static V1.Protocol ToContract(this CoreProtocol protocol) => protocol switch
    {
        CoreProtocol.Vless => V1.Protocol.Vless,
        CoreProtocol.Vmess => V1.Protocol.Vmess,
        CoreProtocol.Trojan => V1.Protocol.Trojan,
        CoreProtocol.Shadowsocks => V1.Protocol.Shadowsocks,
        CoreProtocol.Hysteria2 => V1.Protocol.Hysteria2,
        CoreProtocol.Tuic => V1.Protocol.Tuic,
        CoreProtocol.WireGuard => V1.Protocol.Wireguard,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null),
    };

    public static CoreProtocol ToCore(this V1.Protocol protocol) => protocol switch
    {
        V1.Protocol.Vless => CoreProtocol.Vless,
        V1.Protocol.Vmess => CoreProtocol.Vmess,
        V1.Protocol.Trojan => CoreProtocol.Trojan,
        V1.Protocol.Shadowsocks => CoreProtocol.Shadowsocks,
        V1.Protocol.Hysteria2 => CoreProtocol.Hysteria2,
        V1.Protocol.Tuic => CoreProtocol.Tuic,
        V1.Protocol.Wireguard => CoreProtocol.WireGuard,
        _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, "Протокол не задан"),
    };

    public static V1.SecurityType ToContract(this CoreSecurity security) => security switch
    {
        CoreSecurity.None => V1.SecurityType.None,
        CoreSecurity.Tls => V1.SecurityType.Tls,
        CoreSecurity.Reality => V1.SecurityType.Reality,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, null),
    };

    public static CoreSecurity ToCore(this V1.SecurityType security) => security switch
    {
        V1.SecurityType.None => CoreSecurity.None,
        V1.SecurityType.Tls => CoreSecurity.Tls,
        V1.SecurityType.Reality => CoreSecurity.Reality,
        _ => throw new ArgumentOutOfRangeException(nameof(security), security, "Защита транспорта не задана"),
    };
}
