using System.Globalization;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Вкладки списка серверов.</summary>
public enum ServerListTab
{
    All,
    Favorites,
    Recent,
}

/// <summary>Порядок строк в списке серверов.</summary>
public enum ServerSort
{
    /// <summary>Сначала самые быстрые; недоступные и не измеренные — в конце.</summary>
    Latency,

    Name,
}

/// <summary>Что показать в списке серверов: поиск, вкладка, скрытые протоколы и порядок.</summary>
public sealed record ServerListQuery(
    string Search,
    ServerListTab Tab,
    IReadOnlySet<ProxyProtocol> HiddenProtocols,
    ServerSort Sort)
{
    public static ServerListQuery Default { get; } =
        new(string.Empty, ServerListTab.All, new HashSet<ProxyProtocol>(), ServerSort.Latency);
}

/// <summary>Группа строк под одним заголовком: подписка или «Мои серверы» (Subscription = null).</summary>
public sealed record ServerGroup(ServerSubscription? Subscription, IReadOnlyList<ServerProfile> Servers);

/// <summary>
/// Поиск, вкладки, фильтр протоколов и сортировка списка серверов (docs/design, экран 2).
/// Только отбирает и упорядочивает — сам выбранный сервер не меняет.
/// </summary>
public static class ServerList
{
    /// <summary>Сколько последних выбранных серверов показывать во вкладке «Недавние».</summary>
    public const int RecentLimit = 10;

    /// <summary>Порядок протоколов в фильтре: сначала основные, как в макете.</summary>
    public static IReadOnlyList<ProxyProtocol> ProtocolOrder { get; } =
    [
        ProxyProtocol.Vless,
        ProxyProtocol.Vmess,
        ProxyProtocol.Hysteria2,
        ProxyProtocol.Tuic,
        ProxyProtocol.WireGuard,
        ProxyProtocol.Trojan,
        ProxyProtocol.Shadowsocks,
    ];

    /// <summary>Протоколы, которые есть среди серверов, — только они попадают в фильтр.</summary>
    public static IReadOnlyList<ProxyProtocol> ProtocolsIn(IEnumerable<ServerProfile> servers)
    {
        var present = servers.Select(s => s.Protocol).ToHashSet();
        return ProtocolOrder.Where(present.Contains).ToList();
    }

    /// <summary>
    /// Серверы вкладки по запросу, разложенные по группам. На вкладке «Все» — по подпискам в их
    /// порядке, серверы без подписки последней группой; на остальных — одна группа.
    /// </summary>
    public static IReadOnlyList<ServerGroup> Build(
        IEnumerable<ServerProfile> servers,
        IReadOnlyList<ServerSubscription> subscriptions,
        ServerListQuery query,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        var rows = Filter(servers, query);
        if (query.Tab != ServerListTab.All)
        {
            // «Недавние» — в порядке выбора, свежие сверху: сортировка их не трогает.
            var shown = query.Tab == ServerListTab.Recent ? rows : Sort(rows, query.Sort, culture);
            return shown.Count == 0 ? [] : [new ServerGroup(null, shown)];
        }

        var groups = new List<ServerGroup>();
        foreach (var subscription in subscriptions)
        {
            var own = rows.Where(s => s.SubscriptionId == subscription.Id).ToList();
            if (own.Count > 0)
            {
                groups.Add(new ServerGroup(subscription, Sort(own, query.Sort, culture)));
            }
        }

        var known = subscriptions.Select(s => s.Id).ToHashSet();
        var manual = rows.Where(s => s.SubscriptionId is null || !known.Contains(s.SubscriptionId)).ToList();
        if (manual.Count > 0)
        {
            groups.Add(new ServerGroup(null, Sort(manual, query.Sort, culture)));
        }

        return groups;
    }

    /// <summary>Подходит ли сервер под строку поиска: название, город, код страны, адрес, протокол.</summary>
    public static bool Matches(ServerProfile server, string search)
    {
        ArgumentNullException.ThrowIfNull(server);
        var text = search?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return true;
        }

        return new[] { server.Name, server.City, server.CountryCode, server.Host, ServerFacts.ProtocolName(server.Protocol) }
            .Any(field => field is not null && field.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ServerProfile> Filter(IEnumerable<ServerProfile> servers, ServerListQuery query)
    {
        var rows = servers.Where(s => !query.HiddenProtocols.Contains(s.Protocol) && Matches(s, query.Search));
        return query.Tab switch
        {
            ServerListTab.Favorites => rows.Where(s => s.IsFavorite).ToList(),
            ServerListTab.Recent => rows.Where(s => s.LastUsedAt is not null)
                .OrderByDescending(s => s.LastUsedAt)
                .Take(RecentLimit)
                .ToList(),
            _ => rows.ToList(),
        };
    }

    private static List<ServerProfile> Sort(List<ServerProfile> rows, ServerSort sort, CultureInfo culture)
    {
        var names = StringComparer.Create(culture, ignoreCase: true);
        var ordered = sort == ServerSort.Name
            ? rows.OrderBy(s => s.Name, names)
            : rows.OrderBy(s => s.LatencyMs is null).ThenBy(s => s.LatencyMs).ThenBy(s => s.Name, names);
        return ordered.ToList();
    }
}
