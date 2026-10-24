using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Серверы пользователя и выбранный из них. Выбор только ручной: сам сервер не меняется.</summary>
public interface IServerCatalog
{
    IReadOnlyList<ServerProfile> Servers { get; }

    /// <summary>Подписки, из которых пришли серверы.</summary>
    IReadOnlyList<ServerSubscription> Subscriptions { get; }

    /// <summary>Выбранный сервер; null — серверов нет или ни один не выбран.</summary>
    ServerProfile? Selected { get; }

    event EventHandler? Changed;

    /// <summary>Выбрать сервер. Он же попадает в «Недавние».</summary>
    void Choose(string serverId);

    /// <summary>Отметить сервер звездой или снять отметку.</summary>
    void SetFavorite(string serverId, bool isFavorite);

    /// <summary>Дать серверу свое название вместо названия из ссылки или подписки.</summary>
    void Rename(string serverId, string name);

    /// <summary>Удалить сервер. Если он был выбран — выбранного больше нет: другой выбирает пользователь.</summary>
    void Remove(string serverId);

    /// <summary>Перемерить пинг и обновить списки серверов из подписок.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
