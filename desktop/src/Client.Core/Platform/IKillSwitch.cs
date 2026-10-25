using System.Net;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>Сервер для блока при обрыве: адрес и порт, к которым ядро соединяется мимо туннеля (TCP и UDP).</summary>
public sealed record KillSwitchEndpoint(IPAddress Address, int Port);

/// <summary>
/// Что пропускает блок при обрыве — все остальное закрыто. Петля (loopback), DHCP и обнаружение
/// соседей IPv6 открыты всегда: без них не работают ядро и сеть машины, наружу через них ничего не уходит.
/// </summary>
/// <param name="TunnelInterface">Адаптер туннеля: через него можно все. Правило действует и до появления адаптера; null — туннеля нет.</param>
/// <param name="Servers">Сервер: только к этим адресам и портам соединения идут мимо туннеля.</param>
/// <param name="AllowLan">Локальная сеть разрешена.</param>
/// <param name="LanDiscovery">Обнаружение устройств в локальной сети (mDNS, SSDP — групповые адреса).</param>
/// <param name="BlockOutsideDns">DNS мимо туннеля закрыт, даже к роутеру в разрешенной локальной сети.</param>
public sealed record KillSwitchPolicy(
    string? TunnelInterface,
    IReadOnlyList<KillSwitchEndpoint> Servers,
    bool AllowLan = false,
    bool LanDiscovery = false,
    bool BlockOutsideDns = true);

/// <summary>
/// Блок при обрыве средствами ОС (nftables на Linux, WFP на Windows). Правила живут в ОС, а не в
/// процессе службы: упала служба — блок остается (fail-closed), снимает его только <see cref="Disable"/>.
/// </summary>
public interface IKillSwitch
{
    /// <summary>Чем ставятся правила — для журнала.</summary>
    string Name { get; }

    /// <summary>Правила стоят — этого запуска службы или оставленные прошлым.</summary>
    bool IsEnabled { get; }

    /// <summary>Поставить правила или заменить прежние одним шагом: щели между старыми и новыми нет.</summary>
    void Enable(KillSwitchPolicy policy);

    /// <summary>Снять правила; их нет — ничего не делать.</summary>
    void Disable();
}
