using System.Net;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>Сервер для блока при обрыве: адрес и порт, к которым ядро соединяется мимо туннеля (TCP и UDP).</summary>
public sealed record KillSwitchEndpoint(IPAddress Address, int Port);

/// <summary>
/// Выход мимо туннеля в раздельном режиме: соединения, которые sing-box по правилам обхода (программы,
/// сайты «Напрямую») отправляет напрямую. Брандмауэр узнает их по программе sing-box (WFP) или по
/// метке его выхода (nftables) — другие программы этим путем мимо блока не пройдут.
/// </summary>
/// <param name="Program">Полный путь к sing-box.</param>
/// <param name="Mark">Метка пакетов выхода sing-box (SO_MARK, Linux).</param>
public sealed record DirectExit(string Program, uint Mark);

/// <summary>
/// Поиск адреса сервера самой службой под блоком без туннеля: DoH к закрепленному адресу (TCP). Пропускается только
/// программа службы (WFP) или ее сокет с меткой (nftables, ставит только root) и только к этим адресам и портам — на время
/// поиска.
/// </summary>
/// <param name="Program">Полный путь к программе службы.</param>
/// <param name="Mark">Метка сокета поиска (SO_MARK, Linux).</param>
/// <param name="Endpoints">Адреса и порты DoH.</param>
public sealed record ServiceLookup(string Program, uint Mark, IReadOnlyList<KillSwitchEndpoint> Endpoints);

/// <summary>
/// Что пропускает блок при обрыве — все остальное закрыто. Петля (loopback), DHCP и обнаружение
/// соседей IPv6 открыты всегда: без них не работают ядро и сеть машины, наружу через них ничего не уходит.
/// </summary>
/// <param name="TunnelInterface">Адаптер туннеля: через него можно все. Правило действует и до появления адаптера; null — туннеля нет.</param>
/// <param name="Servers">Сервер: только к этим адресам и портам соединения идут мимо туннеля.</param>
/// <param name="AllowLan">Локальная сеть разрешена.</param>
/// <param name="LanDiscovery">Обнаружение устройств в локальной сети (mDNS, SSDP — групповые адреса).</param>
/// <param name="BlockOutsideDns">DNS мимо туннеля закрыт, даже к роутеру в разрешенной локальной сети.</param>
/// <param name="Direct">Раздельный режим: выход sing-box мимо туннеля открыт целиком, и DNS тоже — обход выбран пользователем; null — обхода нет.</param>
/// <param name="Persistent">
/// «Защита при загрузке»: правила переживают перезагрузку и действуют еще до старта службы (WFP — постоянные
/// фильтры). Где ОС так не умеет (nftables), служба ставит блок сама при запуске, раньше сети.
/// </param>
/// <param name="TunnelDns">
/// Адрес DNS туннеля: пока туннель поднят, а DNS мимо него закрыт при разрешенной локальной сети, запросы DNS к ней
/// (роутер из resolv.conf без systemd-resolved) перенаправляются сюда — в туннель (nftables). На Windows ОС и так
/// спрашивает DNS адаптера туннеля. null — не перенаправлять.
/// </param>
/// <param name="LanDns">
/// Свой DNS в частной сети («DNS» — свой адрес, как у Pi-hole): к нему DNS (53, TCP и UDP) мимо туннеля пропускается — его
/// спрашивает Xray, через сервер частный адрес не достать. Остальное к этому адресу — по <paramref name="AllowLan"/>. null — нет.
/// </param>
/// <param name="Lookup">Поиск адреса сервера службой под блоком (DoH) — только на время поиска; null — нет.</param>
/// <param name="Ipv6Outside">
/// IPv6 мимо туннеля открыт: «Блок при обрыве» выключен, а туннель сам пропускает IPv6 мимо себя («Блокировать IPv6 вне
/// туннеля» выключено, у сервера IPv6 нет) — правила «только туннель» его не закрывают, и это видно на главной. С блоком при
/// обрыве IPv6 мимо туннеля закрыт всегда.
/// </param>
public sealed record KillSwitchPolicy(
    string? TunnelInterface,
    IReadOnlyList<KillSwitchEndpoint> Servers,
    bool AllowLan = false,
    bool LanDiscovery = false,
    bool BlockOutsideDns = true,
    DirectExit? Direct = null,
    bool Persistent = false,
    IPAddress? TunnelDns = null,
    bool Ipv6Outside = false,
    IPAddress? LanDns = null,
    ServiceLookup? Lookup = null)
{
    /// <summary>Сколько адресов сервера держит блок: у ключей фильтров WFP есть предел.</summary>
    public const int MaxServers = 16;
}

/// <summary>
/// Блок при обрыве средствами ОС (nftables на Linux, WFP на Windows). Правила живут в ОС, а не в
/// процессе службы: упала служба — блок остается (fail-closed), снимает его только <see cref="Disable"/>.
/// </summary>
public interface IKillSwitch
{
    /// <summary>Что блок закрыть не смог при последнем включении или снятии (например, ВМ без брандмауэра Hyper-V); все — null.</summary>
    string? Warning => null;

    /// <summary>Чем ставятся правила — для журнала.</summary>
    string Name { get; }

    /// <summary>Правила стоят — этого запуска службы или оставленные прошлым.</summary>
    bool IsEnabled { get; }

    /// <summary>Поставить правила или заменить прежние одним шагом: щели между старыми и новыми нет.</summary>
    void Enable(KillSwitchPolicy policy);

    /// <summary>Снять правила; их нет — ничего не делать.</summary>
    void Disable();
}
