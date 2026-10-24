namespace PrivateGateway.Client.Core;

/// <summary>Протокол подключения к серверу.</summary>
public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    Tuic,
    WireGuard,
}

/// <summary>Защита транспорта поверх протокола.</summary>
public enum TransportSecurity
{
    /// <summary>Без TLS: протокол передает данные как есть.</summary>
    None,

    Tls,

    /// <summary>REALITY — TLS-маскировка Xray.</summary>
    Reality,
}
