namespace PrivateGateway.Client.Core.Servers;

/// <summary>
/// Группа серверов: подписка по адресу, которую программа обновляет сама, или профиль из файла и
/// ссылок без адреса.
/// </summary>
/// <param name="Id">Постоянный идентификатор группы в базе.</param>
/// <param name="Name">Название, которое дал пользователь при добавлении.</param>
/// <param name="UpdatedAt">Когда список последний раз обновился; null — еще не обновлялся.</param>
/// <param name="Address">
/// Адрес подписки без секретной части для подписи («https://example.com/sub/••••»); null — адреса
/// нет, группа не обновляется. Сам адрес — только в хранилище секретов ОС.
/// </param>
/// <param name="LastAttemptAt">Когда последний раз пробовали обновить; null — не пробовали.</param>
/// <param name="LastFailure">Почему последняя попытка не удалась; null — удалась или не было.</param>
/// <param name="LastStatusCode">Код ответа сервера при <see cref="DownloadFailure.Status"/>.</param>
public sealed record ServerSubscription(
    string Id,
    string Name,
    DateTimeOffset? UpdatedAt,
    string? Address = null,
    DateTimeOffset? LastAttemptAt = null,
    DownloadFailure? LastFailure = null,
    int? LastStatusCode = null)
{
    /// <summary>Есть адрес — список можно обновить.</summary>
    public bool CanUpdate => Address is not null;
}
