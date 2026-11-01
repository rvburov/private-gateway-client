using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Причина, по которой HTTP-проба не получила заголовки ответа; это не диагноз самого VPN-сервера.</summary>
public enum ProbeFailure
{
    Timeout,
    NameResolution,
    Tls,
    Connection,
    Proxy,
    Authentication,
    Protocol,
    Network,
}

/// <summary>
/// Ответ пробы и безопасная причина отказа. Адрес запроса, учетные данные и тексты исключений здесь не сохраняются:
/// исключение HttpClient может включать адрес с секретным путем. Любой HTTP-ответ подтверждает доступность пути.
/// </summary>
public sealed record ProbeReachability(int? StatusCode, ProbeFailure? Failure, HttpRequestError? HttpError = null, SocketError? SocketFailure = null)
{
    public bool Reached => StatusCode is not null;

    /// <summary>Для журнала: только фиксированный текст, числовой HTTP-код и системные категории ошибки.</summary>
    public string Description
    {
        get
        {
            if (StatusCode is { } code)
            {
                return "Проверочный адрес ответил: HTTP " + code.ToString(CultureInfo.InvariantCulture);
            }

            var reason = Failure switch
            {
                ProbeFailure.Timeout => "Превышено время ожидания ответа",
                ProbeFailure.NameResolution => "Ошибка разрешения имени (DNS)",
                ProbeFailure.Tls => "Ошибка защищенного соединения (TLS)",
                ProbeFailure.Connection => "Не удалось установить соединение",
                ProbeFailure.Proxy => "Ошибка соединения через локальный прокси ядра",
                ProbeFailure.Authentication => "Ошибка аутентификации HTTP",
                ProbeFailure.Protocol => "Ошибка протокола или неполный HTTP-ответ",
                _ => "Сетевая ошибка при проверке связи",
            };
            return reason + (HttpError is { } http ? "; HTTP-клиент: " + http : string.Empty)
                + (SocketFailure is { } socket ? "; сокет: " + socket : string.Empty);
        }
    }

    public static ProbeReachability Responded(HttpStatusCode code) => new((int)code, null);

    public static ProbeReachability TimedOut() => new(null, ProbeFailure.Timeout);

    public static ProbeReachability Failed(HttpRequestException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var failure = error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => ProbeFailure.NameResolution,
            HttpRequestError.SecureConnectionError => ProbeFailure.Tls,
            HttpRequestError.ConnectionError => ProbeFailure.Connection,
            HttpRequestError.ProxyTunnelError => ProbeFailure.Proxy,
            HttpRequestError.UserAuthenticationError => ProbeFailure.Authentication,
            HttpRequestError.HttpProtocolError or HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded
                or HttpRequestError.VersionNegotiationError or HttpRequestError.ExtendedConnectNotSupported => ProbeFailure.Protocol,
            _ => ProbeFailure.Network,
        };
        SocketError? socket = null;
        for (Exception? nested = error; nested is not null; nested = nested.InnerException)
        {
            if (nested is SocketException cause)
            {
                socket = cause.SocketErrorCode;
                break;
            }
        }

        return new ProbeReachability(null, failure, error.HttpRequestError, socket);
    }
}
