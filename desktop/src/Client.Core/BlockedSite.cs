namespace PrivateGateway.Client.Core;

/// <summary>Реально отклоненные соединения за сегодня: только адрес, счет и последнее время; без пути страницы.</summary>
public sealed record BlockedSite(string Host, int Count, DateTimeOffset LastSeen);
