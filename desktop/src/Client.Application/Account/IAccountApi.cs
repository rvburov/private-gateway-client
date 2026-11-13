using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Account;

/// <summary>
/// API сайта для программы (<see cref="AccountSite"/>): вход устройства, проверка доступа, выход, лента обновлений.
/// Ошибки — <see cref="AccountApiException"/> с причиной, без типов HTTP: слой сценариев сети не знает.
/// </summary>
public interface IAccountApi
{
    /// <summary><c>POST /api/app/login</c>: токен устройства и подтвержденные сведения.</summary>
    /// <exception cref="AccountApiException">Сайт не пустил или не ответил.</exception>
    Task<AccountLogin> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary><c>GET /api/app/status</c>: доступ действует, сайт продлил срок. 401 — <see cref="AccountFailure.InvalidCredentials"/>.</summary>
    /// <exception cref="AccountApiException">Доступ отозван или сайт не ответил.</exception>
    Task<AccountStatus> StatusAsync(string token, CancellationToken cancellationToken = default);

    /// <summary><c>POST /api/app/logout</c>: токен больше не действует. Уже недействующий токен (401) — тоже выход.</summary>
    /// <exception cref="AccountApiException">Сайт не ответил.</exception>
    Task LogoutAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET /api/app/updates?platform=…&amp;channel=…</c>: лента обновлений для ОС <paramref name="platform"/>
    /// (<see cref="DevicePlatform"/>) — байты ответа как есть, не больше 1 МБ: проверяет их служба. 401 —
    /// <see cref="AccountFailure.InvalidCredentials"/>.
    /// </summary>
    /// <exception cref="AccountApiException">Доступ отозван или сайт не ответил.</exception>
    Task<byte[]> UpdatesAsync(string token, string platform, UpdateChannel channel, CancellationToken cancellationToken = default);
}

/// <summary>
/// Сайт не пустил или не ответил. Ни текст, ни причина не содержат токена, пароля, кодов, почты и тела ответа.
/// </summary>
public sealed class AccountApiException : Exception
{
    public AccountApiException()
    {
    }

    public AccountApiException(string message)
        : base(message)
    {
    }

    public AccountApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AccountApiException(AccountFailure failure, int? statusCode = null, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(statusCode is { } code ? $"Сайт: {failure} {code}" : $"Сайт: {failure}", innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public AccountFailure Failure { get; }

    /// <summary>Код ответа сайта; null — ответа не было.</summary>
    public int? StatusCode { get; }

    /// <summary>Для 429 — сколько ждать (<c>Retry-After</c>); иначе null.</summary>
    public TimeSpan? RetryAfter { get; }
}
