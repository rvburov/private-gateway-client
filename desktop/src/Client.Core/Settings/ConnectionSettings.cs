namespace PrivateGateway.Client.Core.Settings;

/// <summary>Подробность журнала ядра (доска 6).</summary>
public enum CoreLogLevel
{
    Warning,
    Info,
    Debug,
}

/// <summary>Как TUN обрабатывает пакеты.</summary>
public enum TunEngine
{
    System,
    Gvisor,
    Mixed,
}

/// <summary>Отпечаток TLS (uTLS): каким браузером клиент выглядит для сети.</summary>
public enum TlsFingerprint
{
    /// <summary>Как в ссылке или подписке; не задан — выбор ядра.</summary>
    Profile,
    Chrome,
    Firefox,
    Safari,
    Random,
}

/// <summary>Переподключение — только к выбранному серверу.</summary>
public enum ReconnectPolicy
{
    /// <summary>Повторы с растущей паузой, не реже раза в 60 секунд.</summary>
    Backoff60Seconds,

    /// <summary>Повторы в течение 5 минут, потом блок и решение за пользователем.</summary>
    UpTo5Minutes,
    Never,
}

/// <summary>
/// «Подключение и ядро» (доска 6): ядро, TUN, соединение, режим прокси. Значения по умолчанию —
/// как в макете: строгие маршруты и определение домена включены, Mux и фрагментация выключены,
/// прокси только для этого компьютера. <paramref name="SystemProxy"/> — в режиме прокси служба на
/// время подключения включает системный прокси ОС.
/// </summary>
public sealed record ConnectionSettings(
    CoreLogLevel CoreLog = CoreLogLevel.Warning,
    TunEngine Stack = TunEngine.System,
    int? Mtu = null,
    bool StrictRoutes = true,
    bool Sniffing = true,
    TlsFingerprint Fingerprint = TlsFingerprint.Profile,
    bool Mux = false,
    bool TlsFragment = false,
    int ConnectTimeoutSeconds = 10,
    ReconnectPolicy Reconnect = ReconnectPolicy.Backoff60Seconds,
    int ProxyPort = ConnectionSettings.DefaultProxyPort,
    bool ProxyAllowLan = false,
    bool SystemProxy = true)
{
    /// <summary>Порт локального входа ядра HTTP + SOCKS5 (mixed).</summary>
    public const int DefaultProxyPort = 10808;

    /// <summary>Свой MTU: от минимального для IPv6 (1280) до обычного Ethernet (1500) — иначе пакеты режутся.</summary>
    public const int MinMtu = 1280;

    public const int MaxMtu = 1500;

    public static ConnectionSettings Default { get; } = new();

    /// <summary>Предлагаемые MTU; null — подбирает служба.</summary>
    public static IReadOnlyList<int?> MtuChoices { get; } = [null, 1500, 1420, 1280];

    /// <summary>Предлагаемые сроки ожидания ответа сервера, секунды.</summary>
    public static IReadOnlyList<int> TimeoutChoices { get; } = [10, 5, 20, 30];
}
