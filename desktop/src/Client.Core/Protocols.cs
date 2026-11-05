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

    /// <summary>Обычный HTTP CONNECT; HTTPS — тот же протокол с TLS до прокси.</summary>
    Http,

    /// <summary>Внешний SOCKS5-прокси.</summary>
    Socks5,
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
