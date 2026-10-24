namespace PrivateGateway.Client.Core.Servers;

/// <summary>
/// Сервер, как его видит интерфейс: название, страна, протокол, адрес. Ключи и пароли здесь не
/// хранятся — они в хранилище ОС и передаются службе только при подключении.
/// </summary>
/// <param name="Id">Постоянный идентификатор профиля в базе.</param>
/// <param name="Name">Название из ссылки или подписки, например «Нидерланды 01».</param>
/// <param name="CountryCode">Код страны ISO 3166-1 alpha-2 для значка; пусто — неизвестно.</param>
/// <param name="Protocol">Протокол подключения.</param>
/// <param name="Security">Защита транспорта: без TLS, TLS или REALITY.</param>
/// <param name="Host">Адрес сервера (домен или IP).</param>
/// <param name="Port">Порт сервера.</param>
/// <param name="Transport">Транспорт для подписи: tcp, ws, grpc, xhttp…</param>
/// <param name="LatencyMs">Последний измеренный пинг; null — не мерили или сервер недоступен.</param>
/// <param name="City">Город для подписи и поиска; null — неизвестен.</param>
/// <param name="SubscriptionId">Подписка, из которой пришел сервер; null — добавлен ссылкой.</param>
/// <param name="IsFavorite">Пользователь отметил сервер звездой.</param>
/// <param name="LastUsedAt">Когда сервер последний раз выбирали; null — не выбирали.</param>
public sealed record ServerProfile(
    string Id,
    string Name,
    string CountryCode,
    ProxyProtocol Protocol,
    TransportSecurity Security,
    string Host,
    int Port,
    string Transport,
    int? LatencyMs,
    string? City = null,
    string? SubscriptionId = null,
    bool IsFavorite = false,
    DateTimeOffset? LastUsedAt = null);

