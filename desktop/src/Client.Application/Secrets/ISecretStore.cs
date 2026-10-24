namespace PrivateGateway.Client.Application.Secrets;

/// <summary>
/// Хранилище секретов ОС: DPAPI на Windows, Связка ключей на macOS, Secret Service на Linux.
/// Ключи серверов и адреса подписок хранятся только здесь — открытым текстом нигде.
/// </summary>
public interface ISecretStore
{
    /// <summary>Имя хранилища для сообщений пользователю.</summary>
    string Name { get; }

    /// <summary>
    /// Хранилище есть и отвечает. Нет — секреты сохранить нельзя: импорт говорит об этом, а не
    /// пишет их в файл.
    /// </summary>
    bool IsAvailable { get; }

    /// <exception cref="InvalidOperationException">Хранилище недоступно или отказало.</exception>
    void Save(string key, string secret);

    /// <returns>null — секрета с таким ключом нет.</returns>
    string? Read(string key);

    /// <summary>Удалить секрет; если его нет — ничего не делать.</summary>
    void Delete(string key);
}
