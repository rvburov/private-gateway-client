namespace PrivateGateway.Client.Core.Account;

/// <summary>
/// Сайт Private Gateway Web: он решает, можно ли пользоваться программой (вход, устройства, отзыв доступа).
/// Адрес — константа: в клиенте нет секретов, и подменить сайт настройкой нельзя.
/// </summary>
public static class AccountSite
{
    /// <summary>Адрес сайта; API входа — <c>/api/app/*</c>.</summary>
    public static readonly Uri Address = new("https://private-gateway.duckdns.org/");

    /// <summary>Страница входа сайта: там администратор настраивает 2FA (ответ 403 <c>setup_required</c>).</summary>
    public static readonly Uri LoginPage = new(Address, "login");

    /// <summary>Страница «Устройства»: там освобождают место (ответ 409 <c>device_limit</c> и <c>device_change_limit</c>).</summary>
    public static readonly Uri DevicesPage = new(Address, "devices");
}

/// <summary>Почему сайт не пустил или не ответил. Текст для пользователя — в интерфейсе (<c>account.error.*</c>).</summary>
public enum AccountFailure
{
    /// <summary>401: при входе — неверные почта или пароль (и любой другой код ошибки 401); при проверке — доступ отозван.</summary>
    InvalidCredentials,

    /// <summary>
    /// 401 <c>code_required</c> при входе: почта и пароль верны, но аккаунт администратора — нужен код 2FA или резервный
    /// код. Не ошибка для пользователя: экран входа переходит ко второму шагу.
    /// </summary>
    CodeRequired,

    /// <summary>400: неверный код 2FA или резервный код.</summary>
    InvalidCode,

    /// <summary>403: у администратора 2FA на сайте не настроена — настроить на сайте.</summary>
    SetupRequired,

    /// <summary>409: у участника занято все место для устройств (и любой другой код ошибки 409) — освободить на сайте.</summary>
    DeviceLimit,

    /// <summary>409 <c>device_change_limit</c>: за 30 дней добавлено больше трех новых устройств.</summary>
    DeviceChangeLimit,

    /// <summary>429: слишком много попыток; ждать — <c>Retry-After</c>.</summary>
    RateLimited,

    /// <summary>422: сайт не принял поля запроса.</summary>
    Validation,

    /// <summary>Нет связи с сайтом: сеть, DNS, TLS, прокси, срок ожидания.</summary>
    Network,

    /// <summary>Сайт ответил ошибкой 5xx, неожиданным кодом или ответ не разобран.</summary>
    Server,

    /// <summary>Хранилище секретов ОС недоступно: токен держать негде, вход не выполняется.</summary>
    Store,
}

/// <summary>Почему показан экран входа, если вход был: отозван на сайте или срок истек без подтверждения.</summary>
public enum AccountMessage
{
    None,

    /// <summary>Сайт ответил 401 на проверку: <c>account.revoked</c>.</summary>
    Revoked,

    /// <summary><c>expires_at</c> прошел, а сайт за это время доступ не подтвердил: <c>account.expired</c>.</summary>
    Expired,
}

/// <summary>Участник сайта: почта и имя для карточки «Аккаунт».</summary>
public sealed record AccountUser(string Email, string? DisplayName);

/// <summary>Это устройство в списке устройств участника на сайте.</summary>
public sealed record AccountDevice(string Name);

/// <summary>Что сайт подтвердил: до какого времени действует вход, кто вошел и под каким устройством.</summary>
public sealed record AccountStatus(DateTimeOffset ExpiresAt, AccountUser User, AccountDevice Device);

/// <summary>Ответ сайта на вход: токен и подтвержденные сведения. Токен — секрет, в строку не попадает.</summary>
public sealed record AccountLogin(string Token, AccountStatus Status)
{
    public override string ToString() => $"AccountLogin {{ Status = {Status} }}";
}

/// <summary>Устройство для сайта: случайный uid установки, имя компьютера, ОС (<see cref="DevicePlatform"/>), версия.</summary>
public sealed record DeviceInfo(string Uid, string Name, string Platform, string AppVersion);

/// <summary>
/// Запрос входа: почта, пароль и не больше одного из кода 2FA и резервного кода. Без кода — первый шаг: участнику его
/// хватает, администратору сайт ответит <see cref="AccountFailure.CodeRequired"/>. Пароль и коды — секреты, в строку
/// не попадают.
/// </summary>
public sealed record LoginRequest(string Email, string Password, string? Code, string? RecoveryCode, DeviceInfo Device)
{
    public override string ToString() => $"LoginRequest {{ Device = {Device} }}";
}

/// <summary>
/// Сохраненные сведения входа — не секрет: до какого времени действует вход, кто вошел, имя устройства на сайте
/// и когда сайт подтвердил доступ последний раз. Хранятся в базе программы вне резервной копии; токен — отдельно,
/// в хранилище секретов ОС.
/// </summary>
public sealed record SavedAccount(DateTimeOffset ExpiresAt, string Email, string? DisplayName, string DeviceName, DateTimeOffset CheckedAt);

/// <summary>Итог попытки входа для экрана входа.</summary>
/// <param name="Failure">null — вошли.</param>
/// <param name="RetryAfterMinutes">Для <see cref="AccountFailure.RateLimited"/> — сколько минут ждать (не меньше 1), иначе 0.</param>
public sealed record LoginOutcome(AccountFailure? Failure, int RetryAfterMinutes = 0)
{
    public static readonly LoginOutcome Success = new((AccountFailure?)null);

    public bool Succeeded => Failure is null;

    /// <summary>Отказ; для 429 — минуты из <c>Retry-After</c>, округленные вверх, минимум 1.</summary>
    public static LoginOutcome Failed(AccountFailure failure, TimeSpan? retryAfter = null) =>
        new(failure, failure == AccountFailure.RateLimited
            ? Math.Max(1, (int)Math.Ceiling((retryAfter ?? TimeSpan.Zero).TotalMinutes))
            : 0);
}

/// <summary>ОС для сайта в формате API: <c>windows</c>, <c>macos</c>, <c>linux</c> (Android и iOS — свои приложения).</summary>
public static class DevicePlatform
{
    public static string Current =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "linux";
}
