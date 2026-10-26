using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Проверка утечек: IP, который видят сайты, — запрос к проверочному адресу, — и сравнение с реальным IP,
/// измеренным напрямую до туннеля; так же по IPv6 (<see cref="CheckIpv6Async"/>); DNS — запросом через сетевую карту мимо
/// туннеля (<see cref="CheckDnsAsync"/>). В режиме VPN запрос идет как у любой программы, без прокси: если трафик
/// уходит мимо туннеля, ответ покажет реальный IP. В режиме прокси — через вход проверки ядра (прямо в выход
/// сервера): через туннель идут только программы с прокси. Совпали IP — трафик идет мимо туннеля. После
/// «Защищено» — если включено «Проверять утечки после подключения», и по просьбе окна («Проверить», экран
/// «Проверка»). Город и сеть выхода не ищутся: для этого IP пришлось бы отдать еще одному сервису.
/// </summary>
public sealed partial class Gateway
{
    /// <summary>
    /// Сколько ждать проверочный адрес напрямую: подключение ждет его вместе с поиском адреса сервера. Не ответил
    /// (адрес закрыт в сети) — подключение задержится на столько, и только в первый раз в этой сети.
    /// </summary>
    private static readonly TimeSpan DirectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Пауза перед второй попыткой проверки после подключения.</summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(3);

    /// <summary>Сколько ждать ответа сервера по IPv6 при подключении: без IPv6 у сервера он обычно отказывает сразу.</summary>
    private static readonly TimeSpan Ipv6Timeout = TimeSpan.FromSeconds(4);

    // Открывает ли сервер (адрес и порт) IPv6 — вывод последней проверки: переподключение берет его, не спрашивая снова.
    private readonly ConcurrentDictionary<string, bool> _serverIpv6 = new();

    /// <summary>
    /// Вывод по ответам проверочного адреса через сервер: по IPv6 ответил адресом IPv6 — сервер открывает IPv6; по IPv4
    /// ответил, а по IPv6 нет — не открывает; не ответил по IPv4 — вывода нет (null): медленным или недоступным бывает и сервер.
    /// </summary>
    public static bool? Ipv6Verdict(string? ip4, string? ip6) =>
        IPAddress.TryParse(ip6, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? true
        : ip4 is null ? null
        : false;

    /// <summary>
    /// Как узнать, открывает ли сервер IPv6: проверочный адрес по IPv4 и по IPv6 сразу — через вход проверки, прямо в выход
    /// сервера (<see cref="Ipv6Verdict"/>). Вывода нет — на это подключение IPv6 закрыт, и вывод не запоминается. Проверочных
    /// адресов нет — null: IPv6 через туннель не идет.
    /// </summary>
    /// <param name="core">Подключение.</param>
    /// <param name="reuse">Взять вывод прошлой проверки этого сервера (переподключение); false — проверить заново.</param>
    private Func<ProbeLink, CancellationToken, Task<bool>>? ServerIpv6(CoreRequest core, bool reuse)
    {
        if (_options.Trace is not { } trace || _options.Trace6 is not { } trace6)
        {
            return null;
        }

        var key = $"{core.Server.Host}:{core.Server.Port}";
        return async (link, cancellationToken) =>
        {
            if (reuse && _serverIpv6.TryGetValue(key, out var known))
            {
                return known;
            }

            var credentials = new NetworkCredential(link.User, link.Password);
            var exitIp = _options.ExitIp ?? LatencyProbe.ExitIpAsync;
            var four = exitIp(new ServerProbe(link.Port, credentials, trace, Ipv6Timeout), cancellationToken);
            var six = exitIp(new ServerProbe(link.Port, credentials, trace6, Ipv6Timeout), cancellationToken);
            switch (Ipv6Verdict(await four.ConfigureAwait(false), await six.ConfigureAwait(false)))
            {
                case true:
                    _serverIpv6[key] = true;
                    _journal.Add(JournalLevel.Info, "service", "Сервер открывает IPv6 — IPv6 идет через туннель");
                    return true;
                case false:
                    _serverIpv6[key] = false;
                    _journal.Add(JournalLevel.Info, "service", core.Protection.BlockIpv6
                        ? "Сервер не открывает IPv6 — IPv6 закрыт, мимо туннеля он не уйдет"
                        : "Сервер не открывает IPv6 — «Блокировать IPv6 вне туннеля» выключено");
                    return false;
                default:
                    _serverIpv6.TryRemove(key, out _);
                    _journal.Add(JournalLevel.Warning, "service", "Проверочный адрес не ответил через сервер — открывает ли он IPv6, неизвестно: на это подключение IPv6 закрыт");
                    return false;
            }
        };
    }

    /// <summary>
    /// IP для сайтов сейчас: «Защищено» — замер и сравнение с реальным (IP и время проверки — в состоянии, итог —
    /// в журнале); иначе — по состоянию: выключено и пауза — виден реальный, подключение — ждет, блокировка и
    /// ошибка — трафик стоит.
    /// </summary>
    /// <param name="reportSilence">Писать в журнал, что проверочный адрес не ответил; false — попытка будет еще.</param>
    public async Task<(CheckState State, string? Reason)> CheckExitAsync(bool reportSilence = true)
    {
        var status = Status;
        if (status.IsPaused || status.State == ConnectionState.Off)
        {
            return (CheckState.Off, null);
        }

        if (status.State != ConnectionState.Connected)
        {
            return (status.State is ConnectionState.Connecting or ConnectionState.Reconnecting ? CheckState.Waiting : CheckState.Stopped, null);
        }

        var session = Volatile.Read(ref _session);
        var vpn = status.Mode == ConnectionMode.Vpn;
        var link = _probe;
        string? ip = null;
        if (_options.Trace is { } trace && (vpn || link is not null))
        {
            // В режиме прокси прямой путь открыт всегда — реальный IP мерить заново: сеть могла смениться. Подключение его
            // не ждет — время, как у пробы.
            if (!vpn)
            {
                _directIp = await DirectIpAsync(Timing.ProbeTimeout).ConfigureAwait(false) ?? _directIp;
            }

            ip = await (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(vpn
                ? new ServerProbe(0, null, trace, Timing.ProbeTimeout)
                : new ServerProbe(link!.Port, new NetworkCredential(link.User, link.Password), trace, Timing.ProbeTimeout), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var direct = _directIp;
        Update(s => session == _session && s.State == ConnectionState.Connected
            ? s with
            {
                Exit = ip is null ? s.Exit : ExitInfo.Unknown with { Ip = ip, DirectIp = direct ?? s.Exit.DirectIp },
                Protection = s.Protection with { CheckedAt = _time.GetUtcNow() },
            }
            : s);
        if (ip is null)
        {
            if (_options.Trace is not null && reportSilence)
            {
                _journal.Add(JournalLevel.Warning, "service", vpn
                    ? "Проверка утечек: проверочный адрес не ответил через туннель — IP для сайтов не измерен"
                    : "Проверка утечек: проверочный адрес не ответил через сервер — IP для сайтов не измерен");
            }

            return (CheckState.Warning, "no_answer");
        }

        if (ip == direct)
        {
            _journal.Add(JournalLevel.Error, "service", "Проверка утечек: IP для сайтов совпадает с реальным — трафик идет мимо туннеля");
            return (CheckState.Warning, "same_ip");
        }

        _journal.Add(JournalLevel.Info, "service", direct is null
            ? "Проверка утечек: IP для сайтов измерен, реальный IP неизвестен — сравнить не с чем"
            : "Проверка утечек: сайты видят IP сервера, а не ваш");
        return (CheckState.Ok, null);
    }

    /// <summary>
    /// Вывод замера IPv6 как у любой программы (<see cref="CheckIpv6Async"/>): ответ адресом IPv6 — реальным (измеренным до
    /// туннеля) или любым, когда у туннеля IPv6 нет (тогда IPv6 в туннель не попадает), — IPv6 идет мимо туннеля (true);
    /// ответ другим адресом через туннель с IPv6 — через туннель (false); нет ответа — вывода нет (null).
    /// </summary>
    public static bool? Ipv6Leaks(string? direct6, string? seen6, bool ipv6Tunnel) =>
        !IPAddress.TryParse(seen6, out var seen) || seen.AddressFamily != AddressFamily.InterNetworkV6 ? null
        : !ipv6Tunnel || (IPAddress.TryParse(direct6, out var real) && real.Equals(seen));

    /// <summary>
    /// Строка IPv6 по состоянию защиты: <c>tunnel</c> — через туннель, <c>outside</c> — мимо туннеля открыт настройками
    /// (режим VPN, «Защищено»), иначе без причины.
    /// </summary>
    public static (CheckState State, string? Reason) Ipv6Row(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return (status.Protection.Ipv6, status.Protection.Ipv6Tunnel ? "tunnel"
            : status is { Mode: ConnectionMode.Vpn, State: ConnectionState.Connected, IsPaused: false, Protection.Ipv6: CheckState.Warning } ? "outside"
            : null);
    }

    /// <summary>
    /// IPv6 для сайтов сейчас. В «Защищено» режима VPN — замер: проверочный адрес по IPv6 без прокси, как у любой программы, и
    /// вывод <see cref="Ipv6Leaks"/>. IPv6 мимо туннеля, хотя настройки его закрывают или ведут в туннель, — «Внимание»
    /// (<c>leak</c>), IPv6 мимо туннеля — в состоянии, ошибка — в журнале; открыт настройками — <c>outside</c>, как по
    /// состоянию. Иначе (нет ответа, через туннель, другое состояние, нет проверочного адреса) — по состоянию.
    /// </summary>
    public async Task<(CheckState State, string? Reason)> CheckIpv6Async()
    {
        var status = Status;
        if (status is not { Mode: ConnectionMode.Vpn, State: ConnectionState.Connected, IsPaused: false } || _options.Trace6 is not { } trace6)
        {
            return Ipv6Row(status);
        }

        // IPv6 по настройкам и туннелю, без вывода замера.
        ConnectionStatus Policy(ConnectionStatus s) => Honest(s with { Protection = s.Protection with { Ipv6 = ProtectionChecks.For(s.State, s.Mode).Ipv6 } });

        var session = Volatile.Read(ref _session);
        var seen = await (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(new ServerProbe(0, null, trace6, Timing.ProbeTimeout), CancellationToken.None)
            .ConfigureAwait(false);
        var leaks = Ipv6Leaks(_directIp6, seen, Policy(status).Protection.Ipv6Tunnel) == true;

        // Утечка — в состоянии, пока подключение то же; ее нет — IPv6 снова по состоянию (прошлый замер мог ее показать).
        Update(s => session != _session || s.State != ConnectionState.Connected ? s
            : leaks ? s with { Protection = s.Protection with { Ipv6 = CheckState.Warning, Ipv6Tunnel = false } }
            : Policy(s));
        var policy = Ipv6Row(Policy(Status));
        if (!leaks)
        {
            return policy;
        }

        _journal.Add(JournalLevel.Error, "service", "Проверка утечек: сайты видят ваш адрес IPv6 — IPv6 идет мимо туннеля");
        return policy.Reason == "outside" ? policy : (CheckState.Warning, "leak");
    }

    /// <summary>
    /// DNS мимо туннеля сейчас. В «Защищено» режима VPN — замер: запрос DNS через сетевую карту мимо маршрутов TUN, как его шлет
    /// DNS ОС по каждой карте (<see cref="DnsProbe"/>). Ответ пришел — путь мимо туннеля открыт, хотя правила его закрывают
    /// (к резолверу в интернете не открывает ни одна настройка): «Внимание» (<c>dns_open</c>), DNS «частично» в состоянии, ошибка в
    /// журнале. Закрыто или мерить нечем — по состоянию (<see cref="DnsReason"/>); следующий замер без ответа возвращает строку и
    /// чип к состоянию.
    /// </summary>
    public async Task<(CheckState State, string? Reason)> CheckDnsAsync()
    {
        var status = Status;
        if (status is { Mode: ConnectionMode.Vpn, State: ConnectionState.Connected, IsPaused: false } && _options.OutsideDns is { } resolver)
        {
            var session = Volatile.Read(ref _session);
            if (await (_options.DnsPastTunnel ?? DnsThroughCardAsync)(resolver, CancellationToken.None).ConfigureAwait(false) is { } open)
            {
                Update(s => session != _session || s.State != ConnectionState.Connected ? s
                    : open ? s with { Protection = s.Protection with { Dns = CheckState.Warning } }
                    : Honest(s with { Protection = s.Protection with { Dns = ProtectionChecks.For(s.State, s.Mode).Dns } }));
                if (open)
                {
                    _journal.Add(JournalLevel.Error, "service", "Проверка утечек: запрос DNS через сетевую карту прошел мимо туннеля — DNS мимо туннеля открыт");
                    return (CheckState.Warning, "dns_open");
                }
            }
        }

        return (Status.Protection.Dns, DnsReason);
    }

    /// <summary>
    /// Запрос DNS через карту маршрута по умолчанию, не туннель (<see cref="DnsProbe.PastTunnelAsync"/>); маршрутов служба не
    /// читает или такой карты нет — null.
    /// </summary>
    private async Task<bool?> DnsThroughCardAsync(IPEndPoint resolver, CancellationToken cancellationToken)
    {
        string? adapter;
        try
        {
            adapter = _options.Routes?.DefaultRoutes().FirstOrDefault(r => !r.IsTunnel)?.Adapter;
        }
        catch (IOException)
        {
            adapter = null;
        }

        return adapter is null ? null : await DnsProbe.PastTunnelAsync(resolver, adapter, DirectTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>«Защищено»: проверять связь с сервером и, если включено, проверить утечки.</summary>
    private void AfterConnected(int session)
    {
        StartWatch(session);
        if (_request is { Protection.CheckLeaksAfterConnect: true } && _options.Trace is not null)
        {
            _ = LeakCheckAsync();
        }
    }

    private async Task LeakCheckAsync()
    {
        try
        {
            // Первый запрос через только что поднятый туннель на занятой машине бывает долгим: не ответил — еще раз.
            var session = Volatile.Read(ref _session);
            if ((await CheckExitAsync(reportSilence: false).ConfigureAwait(false)).Reason == "no_answer" && session == Volatile.Read(ref _session))
            {
                await Task.Delay(RetryPause, _time).ConfigureAwait(false);
                await CheckExitAsync().ConfigureAwait(false);
            }

            await CheckIpv6Async().ConfigureAwait(false);
            await CheckDnsAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
    }

    /// <summary>
    /// Реальный IP — напрямую, мимо ядра, не дольше <paramref name="timeout"/> (по умолчанию — сколько подключение готово ждать);
    /// проверочного адреса нет или он не ответил — null.
    /// </summary>
    private Task<string?> DirectIpAsync(TimeSpan? timeout = null) => DirectIpAsync(_options.Trace, timeout ?? DirectTimeout);

    /// <summary>Реальные IP по IPv4 и по IPv6 — сразу, напрямую (<see cref="DirectIpAsync(TimeSpan?)"/>); адреса IPv6 нет — null.</summary>
    private async Task<(string? Ip, string? Ip6)> DirectIpsAsync()
    {
        var ip = DirectIpAsync();
        var ip6 = DirectIpAsync(_options.Trace6, DirectTimeout);
        return (await ip.ConfigureAwait(false),
            IPAddress.TryParse(await ip6.ConfigureAwait(false), out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ToString() : null);
    }

    private async Task<string?> DirectIpAsync(Uri? trace, TimeSpan timeout) => trace is not null
        ? await (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(new ServerProbe(0, null, trace, timeout), CancellationToken.None).ConfigureAwait(false)
        : null;
}
