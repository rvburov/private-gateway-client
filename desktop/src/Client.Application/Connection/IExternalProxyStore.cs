using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>Сохранение внешнего прокси: метаданные отдельно от учетных данных в хранилище ОС.</summary>
public interface IExternalProxyStore
{
    bool CanStoreSecrets { get; }

    string SecretStoreName { get; }

    /// <summary>Нет обязательных учетных данных или хранилище не отвечает — null, без перехода к анонимной авторизации.</summary>
    ServerCredentials? ReadCredentials();

    /// <summary>Сохранить целиком валидную форму. Ошибка не раскрывает введенные значения и не меняет прежний профиль.</summary>
    void Save(ExternalProxySettings settings, string username, string password);

    /// <summary>Готовое описание для существующего IPC либо null: адрес или обязательные секреты не готовы.</summary>
    ServerDefinition? Definition();
}
