using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Notifications;

/// <summary>О чем бывают уведомления (доски 19 и 22).</summary>
public enum NoticeKind
{
    /// <summary>«Защищено» сменилось «Переподключением»: связь с сервером пропала.</summary>
    ConnectionLost,

    /// <summary>Попытки кончились: сервер не отвечает — «Блокировка» или «Ошибка» с PGC-NET-001.</summary>
    ServerDown,

    /// <summary>Попытки не проходят сразу после смены сети — похоже, сеть ждет вход через браузер.</summary>
    WifiSignIn,

    /// <summary>Служба пропала, пока защита была включена, и не вернулась за <see cref="NotificationCenter.ServiceGrace"/>.</summary>
    ServiceStopped,

    /// <summary>Защита не включилась или остановилась по другой причине — текст по коду ошибки.</summary>
    ProtectionFailed,

    /// <summary>Защита включилась или вернулась.</summary>
    Connected,

    /// <summary>Защиту выключили.</summary>
    Disconnected,

    /// <summary>Выбранный сервер пропал из подписки.</summary>
    ServerGone,

    /// <summary>У выбранного сервера в подписке новый адрес или ключи.</summary>
    ServerChanged,

    /// <summary>Подписка не обновилась по расписанию — прошлый список оставлен.</summary>
    ServersNotUpdated,

    /// <summary>Проверка по расписанию нашла новую версию программы.</summary>
    UpdateAvailable,

    /// <summary>Во время подключения запустилась другая программа VPN или прокси (решение 507).</summary>
    OtherClient,
}

/// <summary>Кнопки уведомления; надписи — <c>notify.action.*</c>.</summary>
public enum NoticeAction
{
    /// <summary>«Открыть»: показать окно.</summary>
    Open,

    /// <summary>«Сменить сервер»: окно, экран «Серверы».</summary>
    ChangeServer,

    /// <summary>«Выбрать сервер»: окно, экран «Серверы».</summary>
    ChooseServer,

    /// <summary>«Выбрать другой»: окно, экран «Серверы».</summary>
    ChooseOther,

    /// <summary>«Повторить»: подключиться к выбранному серверу снова.</summary>
    Retry,

    /// <summary>«Войти»: пауза для входа в Wi-Fi и страница входа в сеть.</summary>
    SignIn,

    /// <summary>«Позже»: закрыть уведомление.</summary>
    Later,

    /// <summary>«Перезапустить»: найти службу заново и подключиться.</summary>
    Restart,

    /// <summary>«Подключить»: включить защиту.</summary>
    Connect,

    /// <summary>«Открыть» у подписок: окно, экран «Профили и подписки».</summary>
    OpenSubscriptions,

    /// <summary>«Обновить»: окно, «О программе»; служба ставит сама — загрузка начинается сразу.</summary>
    Update,
}

/// <summary>
/// Что сказать: событие, ключ текста и кнопки (не больше двух — доска 22). Тексты окно берет из локализации:
/// <c>{0}</c> — сервер, <c>{1}</c> — подписка, <c>{2}</c> — причина (код ошибки или неудача загрузки), <c>{3}</c> — другая программа
/// VPN; в заголовке
/// <c>{0}</c> — версия обновления. IP-адресов и имен сетей Wi-Fi в уведомлениях нет.
/// </summary>
public sealed record Notice(NoticeKind Kind, string TextKey, IReadOnlyList<NoticeAction> Actions)
{
    public string? Server { get; init; }

    public string? Subscription { get; init; }

    /// <summary>Названия других программ VPN и прокси (<see cref="NoticeKind.OtherClient"/>) — <c>{3}</c> в тексте.</summary>
    public string? Program { get; init; }

    /// <summary>Код ошибки защиты (<see cref="Core.ErrorCodes"/>).</summary>
    public string? Code { get; init; }

    /// <summary>Почему подписка не обновилась и код ответа сервера подписки.</summary>
    public DownloadFailure? Failure { get; init; }

    public int? StatusCode { get; init; }

    /// <summary>Найденная версия программы (<see cref="NoticeKind.UpdateAvailable"/>).</summary>
    public string? Version { get; init; }

    /// <summary>Важное (обрыв, блокировка интернета, вход в Wi-Fi, сбой службы) — со звуком; остальные — обычные, без звука.</summary>
    public bool IsImportant => Kind is NoticeKind.ConnectionLost or NoticeKind.ServerDown or NoticeKind.WifiSignIn
        or NoticeKind.ServiceStopped or NoticeKind.ProtectionFailed or NoticeKind.OtherClient;

    /// <summary>
    /// Метка: новое уведомление с той же меткой заменяет прежнее в центре уведомлений ОС — о защите там одно, последнее.
    /// </summary>
    public string Tag => Kind switch
    {
        NoticeKind.ServerGone or NoticeKind.ServerChanged or NoticeKind.ServersNotUpdated => "servers-" + (Subscription ?? string.Empty),
        NoticeKind.UpdateAvailable => UpdateTag,
        _ => ProtectionTag,
    };

    /// <summary>Метка уведомлений о защите: обрыв, сервер, служба, подключение.</summary>
    public const string ProtectionTag = "protection";

    /// <summary>Метка уведомления «Доступно обновление».</summary>
    public const string UpdateTag = "update";
}

/// <summary>Готовый текст уведомления на языке пользователя.</summary>
public sealed record NoticeText(string Title, string Body, IReadOnlyList<NoticeButton> Buttons);

public sealed record NoticeButton(NoticeAction Action, string Label);

/// <summary>Нажали уведомление с меткой <paramref name="Tag"/>: кнопку или само уведомление (<paramref name="Action"/> — null).</summary>
public sealed record NoticeReply(string Tag, NoticeAction? Action);
