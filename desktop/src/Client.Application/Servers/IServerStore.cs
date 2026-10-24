using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Итог сохранения: сколько серверов добавлено и сколько пропущено как дубли.</summary>
public sealed record ImportOutcome(int Added, int Duplicates);

/// <summary>
/// Свои серверы пользователя: сохранить разобранные из ссылок или подписки и достать полное
/// описание с секретами для подключения. Секреты — только в хранилище ОС.
/// </summary>
public interface IServerStore
{
    /// <summary>Хранилище секретов ОС доступно — без него серверы сохранить нельзя.</summary>
    bool CanStoreSecrets { get; }

    /// <summary>Имя хранилища секретов для сообщений пользователю.</summary>
    string SecretStoreName { get; }

    /// <summary>Сколько из этих серверов уже есть (тот же адрес, транспорт, защита и данные доступа).</summary>
    int CountDuplicates(IReadOnlyList<ServerDefinition> servers);

    /// <summary>
    /// Сохранить серверы, дубли пропустить. С названием — отдельной группой (профилем), без —
    /// в «Мои серверы». Если выбранного сервера нет, выбирается первый добавленный.
    /// </summary>
    /// <exception cref="InvalidOperationException">Хранилище секретов недоступно — ничего не сохранено.</exception>
    ImportOutcome Add(IReadOnlyList<ServerDefinition> servers, string? groupName);

    /// <summary>Полное описание сервера с секретами — для подключения; null — сервера нет.</summary>
    ServerDefinition? Definition(string serverId);
}
