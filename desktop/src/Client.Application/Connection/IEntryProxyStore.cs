using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>Входной SOCKS5 для профилей пакета цепочки; учетные данные читает из хранилища ОС.</summary>
public interface IEntryProxyStore
{
    /// <summary>Импортировать серверы и связать их с входным прокси. Пакет уже полностью проверен.</summary>
    ImportOutcome Import(EntryChainBundle bundle, string? groupName);

    /// <summary>Обновить связанную группу только если адрес ответа всё ещё актуален; null при смене подписки.</summary>
    SubscriptionUpdate? Update(string subscriptionId, Uri expectedUrl, EntryChainBundle bundle);

    /// <summary>Прокси для первого участка выбранного сервера; null для обычных профилей.</summary>
    ServerDefinition? DefinitionFor(string serverId);

    /// <summary>Подписка входного узла и ее SOCKS5; null только для несвязанного адреса.</summary>
    EntryChainSubscription? SubscriptionFor(Uri url);
}

/// <summary>Полный адрес подписки читается из хранилища секретов только на время загрузки.</summary>
public sealed record EntryChainSubscription(string NodeId, Uri Url, ServerDefinition Proxy)
{
    public override string ToString() => $"EntryChainSubscription {{ NodeId = {NodeId} }}";
}
