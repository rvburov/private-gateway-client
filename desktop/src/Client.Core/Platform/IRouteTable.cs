namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Маршрут ко всему интернету в любой таблице маршрутизации: 0.0.0.0/0 и ::/0 или их половины
/// (0.0.0.0/1 и 128.0.0.0/1, ::/1 и 8000::/1 — так VPN перебивает основной маршрут, не трогая его).
/// </summary>
/// <param name="Adapter">Имя адаптера, через который идет маршрут.</param>
/// <param name="IsTunnel">Адаптер — туннель (TUN, WireGuard, PPP, TAP), а не сетевая карта.</param>
public sealed record DefaultRoute(string Adapter, bool IsTunnel);

/// <summary>Маршруты машины — чтобы найти чужой VPN. Только читает и ничего не меняет.</summary>
public interface IRouteTable
{
    /// <summary>Маршруты ко всему интернету через включенные адаптеры; прочитать не вышло — <see cref="IOException"/>.</summary>
    IReadOnlyList<DefaultRoute> DefaultRoutes();
}

/// <summary>
/// Чужой VPN — туннель другой программы с маршрутом ко всему интернету: включить вместе с ним свой
/// туннель — трафик может пойти мимо него. Свой туннель (адаптер sing-box) чужим не считается.
/// </summary>
public static class OtherVpn
{
    /// <summary>Имена адаптеров чужих туннелей, по алфавиту и без повторов; пусто — чужого VPN нет.</summary>
    public static IReadOnlyList<string> Find(IEnumerable<DefaultRoute> routes, string? ownAdapter) =>
    [
        .. routes
            .Where(r => r.IsTunnel && !string.Equals(r.Adapter, ownAdapter, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Adapter)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal),
    ];
}
