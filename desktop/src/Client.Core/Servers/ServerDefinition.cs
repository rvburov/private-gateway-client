namespace PrivateGateway.Client.Core.Servers;

/// <summary>Транспорт поверх TCP/UDP — как в контракте IPC (TransportType).</summary>
public enum TransportKind
{
    Tcp,
    WebSocket,
    Grpc,
    HttpUpgrade,
    Xhttp,
    Quic,

    /// <summary>Чистый UDP — у WireGuard.</summary>
    Udp,
}

/// <summary>Транспорт: путь и заголовок Host для WS/HTTPUpgrade/XHTTP, имя сервиса для gRPC.</summary>
public sealed record TransportOptions(TransportKind Kind, string? Path = null, string? Host = null, string? ServiceName = null)
{
    public static TransportOptions Tcp { get; } = new(TransportKind.Tcp);

    public static TransportOptions Quic { get; } = new(TransportKind.Quic);

    public static TransportOptions Udp { get; } = new(TransportKind.Udp);
}

/// <summary>
/// Защита транспорта: TLS или REALITY с маскировкой (SNI) и отпечатком uTLS. Открытый ключ REALITY
/// — не секрет: он есть у каждого клиента сервера.
/// </summary>
public sealed record SecurityOptions(
    TransportSecurity Kind,
    string? Sni = null,
    string? Fingerprint = null,
    IReadOnlyList<string>? Alpn = null,
    bool AllowInsecure = false,
    string? RealityPublicKey = null,
    string? RealityShortId = null,
    string? RealitySpiderX = null)
{
    public static SecurityOptions None { get; } = new(TransportSecurity.None);
}

/// <summary>Несекретные параметры протокола: flow VLESS, шифр VMess/Shadowsocks, обфускация, адреса WireGuard.</summary>
public sealed record ProtocolOptions(
    string? Flow = null,
    string? Cipher = null,
    string? Obfs = null,
    string? CongestionControl = null,
    string? PeerPublicKey = null,
    IReadOnlyList<string>? Addresses = null,
    int? Mtu = null)
{
    public static ProtocolOptions Empty { get; } = new();
}

/// <summary>
/// Секретная часть описания сервера — то, что дает доступ к нему. Хранится только в хранилище
/// ОС, в журнал и подписи не попадает: <see cref="ToString"/> ничего не раскрывает.
/// </summary>
public sealed record ServerCredentials(
    string? Uuid = null,
    string? Password = null,
    string? ObfsPassword = null,
    string? PrivateKey = null,
    string? PresharedKey = null)
{
    public override string ToString() => "ServerCredentials { *** }";
}

/// <summary>
/// Полное описание сервера из ссылки или подписки — то, что служба получает по контракту IPC
/// (ServerEndpoint) и из чего сама собирает конфиг ядра.
/// </summary>
/// <param name="Name">Название из ссылки (часть после #); без него — адрес сервера.</param>
/// <param name="CountryCode">Код страны из флага в названии; пусто — неизвестно.</param>
/// <param name="Protocol">Протокол подключения.</param>
/// <param name="Host">Адрес сервера: домен или IP (IPv6 без скобок).</param>
/// <param name="Port">Порт сервера.</param>
/// <param name="Transport">Транспорт поверх TCP/UDP.</param>
/// <param name="Security">TLS, REALITY или без защиты.</param>
/// <param name="Options">Несекретные параметры протокола.</param>
/// <param name="Credentials">Секретная часть: UUID, пароли, ключи.</param>
public sealed record ServerDefinition(
    string Name,
    string CountryCode,
    ProxyProtocol Protocol,
    string Host,
    int Port,
    TransportOptions Transport,
    SecurityOptions Security,
    ProtocolOptions Options,
    ServerCredentials Credentials);
