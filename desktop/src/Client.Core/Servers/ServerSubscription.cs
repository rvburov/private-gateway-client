namespace PrivateGateway.Client.Core.Servers;

/// <summary>Подписка — список серверов по ссылке, который программа обновляет сама.</summary>
/// <param name="Id">Постоянный идентификатор подписки в базе.</param>
/// <param name="Name">Название, которое дал пользователь при добавлении.</param>
/// <param name="UpdatedAt">Когда список последний раз обновился; null — еще не обновлялся.</param>
public sealed record ServerSubscription(string Id, string Name, DateTimeOffset? UpdatedAt);
