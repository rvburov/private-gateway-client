namespace PrivateGateway.Client.Application.Connection;

/// <summary>Фактическое применение системного прокси: настройка сама по себе не означает, что ОС ее приняла.</summary>
public enum UserProxyState
{
    Off,
    Waiting,
    Applied,
    Held,
    Restored,
    ChangedByOther,
    ServiceManaged,
    Unsupported,
    Error,
}

/// <summary>Порт относится к подтвержденному входу службы; подробности ошибки ОС в интерфейс не передаются.</summary>
public sealed record UserProxyStatus(UserProxyState State, int? Port = null);

/// <summary>
/// Управление прокси пользователя окна. Настройки Windows меняет реализация Infrastructure; приложение и окно
/// только запускают наблюдение и показывают результат. Ошибка не превращается в обещание примененной настройки.
/// </summary>
public interface IUserProxyController
{
    UserProxyStatus Status { get; }

    event EventHandler<UserProxyStatus>? Changed;

    void Start();

    /// <summary>Нормальный выход окна: служба пользователя завершится вместе с ним, ее локальный прокси нужно убрать.</summary>
    void Shutdown();
}
