namespace PrivateGateway.Client.Core.Servers;

/// <summary>
/// Почему подписка не загрузилась или не обновилась; текст для пользователя — в интерфейсе, на его
/// языке. Хранится у подписки как причина последней неудачной попытки.
/// </summary>
public enum DownloadFailure
{
    Network,
    NameResolution,
    Connection,
    Tls,
    Timeout,

    /// <summary>Сервер ответил не 2xx; код ответа хранится рядом.</summary>
    Status,
    TooLarge,

    /// <summary>Загрузилось, но ни одного сервера не разобрано — прошлый список оставлен.</summary>
    NoServers,

    /// <summary>Адреса подписки нет в хранилище секретов ОС.</summary>
    NoAddress,

    /// <summary>Ядро разрешило бы имя подписки через DNS мимо туннеля: такая загрузка запрещена.</summary>
    UnsafeDns,
}
