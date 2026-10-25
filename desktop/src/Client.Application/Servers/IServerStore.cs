using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Итог сохранения: сколько серверов добавлено и сколько пропущено как дубли.</summary>
public sealed record ImportOutcome(int Added, int Duplicates);

/// <summary>Что стало с выбранным сервером после обновления его подписки.</summary>
public enum SelectedServerChange
{
    /// <summary>Выбранный сервер на месте или он не из этой подписки.</summary>
    None,

    /// <summary>Пропал из подписки; оставлен в списке, пока выбран.</summary>
    Missing,

    /// <summary>Пропал, но в подписке есть сервер с тем же названием — у него другой адрес или ключи.</summary>
    Changed,
}

/// <summary>
/// Итог обновления подписки: сколько серверов добавлено, удалено, осталось и что с выбранным.
/// </summary>
public sealed record SubscriptionUpdate(int Added, int Removed, int Kept, SelectedServerChange Selected);

/// <summary>Сервер в резервной копии: полное описание и отметки пользователя.</summary>
/// <param name="Definition">Описание; без ключей в копии — с пустыми <see cref="ServerDefinition.Credentials"/>.</param>
/// <param name="CustomName">Свое название пользователя; null — название из ссылки.</param>
/// <param name="IsFavorite">Отмечен звездой.</param>
/// <param name="IsSelected">Выбранный сервер.</param>
public sealed record SavedServer(ServerDefinition Definition, string? CustomName = null, bool IsFavorite = false, bool IsSelected = false);

/// <summary>Группа в резервной копии: название (null — «Мои серверы»), адрес подписки и серверы.</summary>
public sealed record SavedGroup(string? Name, Uri? SubscriptionUrl, IReadOnlyList<SavedServer> Servers);

/// <summary>Итог восстановления: сколько серверов вернулось и сколько пропущено — у них нет ключей.</summary>
public sealed record RestoreOutcome(int Restored, int WithoutKeys);

/// <summary>
/// Свои серверы пользователя: сохранить разобранные из ссылок или подписки, обновить подписку,
/// достать полное описание с секретами для подключения. Секреты — только в хранилище ОС.
/// </summary>
public interface IServerStore : IServerCatalog
{
    /// <summary>Хранилище секретов ОС доступно — без него серверы сохранить нельзя.</summary>
    bool CanStoreSecrets { get; }

    /// <summary>Имя хранилища секретов для сообщений пользователю.</summary>
    string SecretStoreName { get; }

    /// <summary>Сколько из этих серверов уже есть (тот же адрес, транспорт, защита и данные доступа).</summary>
    int CountDuplicates(IReadOnlyList<ServerDefinition> servers);

    /// <summary>
    /// Сохранить серверы, дубли пропустить. С названием — отдельной группой (профилем), без —
    /// в «Мои серверы». Адрес подписки (в нем токен доступа) — секрет группы в хранилище ОС.
    /// Если выбранного сервера нет, выбирается первый добавленный.
    /// </summary>
    /// <exception cref="InvalidOperationException">Хранилище секретов недоступно — ничего не сохранено.</exception>
    ImportOutcome Add(IReadOnlyList<ServerDefinition> servers, string? groupName, Uri? subscriptionUrl = null);

    /// <summary>Записать измеренную задержку: null — сервер не ответил.</summary>
    void SetLatency(IReadOnlyDictionary<string, int?> latency);

    /// <summary>Свой ли это сервер (добавлен ссылкой или подпиской), не читая ключа из хранилища ОС.</summary>
    bool Contains(string serverId);

    /// <summary>Полное описание сервера с секретами — для подключения; null — сервера нет.</summary>
    ServerDefinition? Definition(string serverId);

    /// <summary>Адрес подписки целиком, из хранилища ОС; null — у группы адреса нет.</summary>
    Uri? SubscriptionUrl(string subscriptionId);

    /// <summary>
    /// Заменить серверы подписки новым списком. Совпавшие (тот же адрес и данные доступа) остаются
    /// со своим избранным, названием пользователя и временем выбора; новых — добавить, пропавших —
    /// удалить. Выбранный сервер не удаляется и не подменяется: пропал — помечается и остается.
    /// Серверы, которые уже есть в других группах, не дублируются.
    /// </summary>
    /// <exception cref="ArgumentException">Пустой список: так подписку не обновляют — оставить прошлый.</exception>
    /// <exception cref="InvalidOperationException">Хранилище секретов недоступно — ничего не изменено.</exception>
    SubscriptionUpdate ApplyUpdate(string subscriptionId, IReadOnlyList<ServerDefinition> servers);

    /// <summary>Обновить не удалось: запомнить причину и время, прошлый список не трогать.</summary>
    void MarkUpdateFailed(string subscriptionId, DownloadFailure failure, int? statusCode = null);

    /// <summary>
    /// Новое название группы и, если задан, новый адрес подписки (у группы без адреса он появится).
    /// </summary>
    /// <exception cref="InvalidOperationException">Новый адрес некуда сохранить: хранилища секретов нет.</exception>
    void EditSubscription(string subscriptionId, string name, Uri? newUrl = null);

    /// <summary>
    /// Удалить группу со всеми ее серверами и секретами; null — «Мои серверы» (серверы без группы).
    /// Выбранный сервер из этой группы перестает быть выбранным.
    /// </summary>
    void RemoveSubscription(string? subscriptionId);

    /// <summary>Все группы и серверы с ключами и отметками — для резервной копии.</summary>
    IReadOnlyList<SavedGroup> Snapshot();

    /// <summary>
    /// Заменить все группы и серверы сохраненными в копии — все или ничего. Серверу без ключей в
    /// копии достаются ключи такого же сервера здесь; такого нет — сервер пропускается: без ключей
    /// к нему не подключиться (серверы подписки вернутся с ее обновлением). Подписки с адресом
    /// считаются необновленными — обновятся при ближайшей проверке.
    /// </summary>
    /// <exception cref="InvalidOperationException">Хранилище секретов недоступно — ничего не изменено.</exception>
    RestoreOutcome Restore(IReadOnlyList<SavedGroup> groups);
}
