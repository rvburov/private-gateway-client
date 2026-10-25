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
/// измеренным напрямую до туннеля. В режиме VPN запрос идет как у любой программы, без прокси: если трафик
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
    private async Task<string?> DirectIpAsync(TimeSpan? timeout = null) => _options.Trace is { } trace
        ? await (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(new ServerProbe(0, null, trace, timeout ?? DirectTimeout), CancellationToken.None)
            .ConfigureAwait(false)
        : null;
}
