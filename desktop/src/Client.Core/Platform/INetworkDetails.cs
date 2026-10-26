using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Подробности сети ОС для сетевого инспектора, которых нет в общих сведениях .NET об адаптерах: вся таблица маршрутов и
/// аренда DHCP. Только читает и ничего не меняет.
/// </summary>
public interface INetworkDetails
{
    /// <summary>Маршруты основной таблицы, IPv4 и IPv6; прочитать не вышло — <see cref="IOException"/>.</summary>
    IReadOnlyList<NetworkRoute> Routes();

    /// <summary>Аренда DHCP адаптера <paramref name="adapter"/>; адрес задан вручную или ОС не говорит — null.</summary>
    DhcpLease? Dhcp(string adapter);
}
