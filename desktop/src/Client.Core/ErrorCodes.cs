namespace PrivateGateway.Client.Core;

/// <summary>
/// Каталог кодов ошибок (docs/architecture/errors.md). Служба присылает код, интерфейс показывает
/// свой текст на языке пользователя; в журнале — код и подробности без секретов.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Служба не запущена или не установлена.</summary>
    public const string ServiceNotRunning = "PGC-SVC-001";

    /// <summary>Ядро не запустилось или остановилось само.</summary>
    public const string CoreFailed = "PGC-SVC-002";

    /// <summary>Запрос не разобран: незаданные значения, данные не того протокола.</summary>
    public const string BadRequest = "PGC-SVC-003";

    /// <summary>Режиму VPN нужны права администратора: служба запущена без них.</summary>
    public const string AdminRequired = "PGC-SVC-004";

    /// <summary>Ядро за сервер не возьмется: Hysteria2 и TUIC — до второго ядра, нет ключа, транспорт не тот.</summary>
    public const string UnsupportedServer = "PGC-SVC-005";

    /// <summary>Режим VPN в этой сборке службы еще не готов (TUN и брандмауэр — этап 3).</summary>
    public const string VpnNotReady = "PGC-SVC-006";

    /// <summary>Сервер не отвечает.</summary>
    public const string ServerNotResponding = "PGC-NET-001";

    /// <summary>Работает другой VPN или системный прокси.</summary>
    public const string OtherVpn = "PGC-NET-010";
}
