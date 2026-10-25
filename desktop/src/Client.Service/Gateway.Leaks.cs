using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
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

    /// <summary>
    /// IP для сайтов сейчас: «Защищено» — замер и сравнение с реальным (IP и время проверки — в состоянии, итог —
    /// в журнале); иначе — по состоянию: выключено и пауза — виден реальный, подключение — ждет, блокировка и
    /// ошибка — трафик стоит.
    /// </summary>
    public async Task<(CheckState State, string? Reason)> CheckExitAsync()
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
            // В режиме прокси прямой путь открыт всегда — реальный IP мерить заново: сеть могла смениться.
            if (!vpn)
            {
                _directIp = await DirectIpAsync().ConfigureAwait(false) ?? _directIp;
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
            if (_options.Trace is not null)
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
            await CheckExitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
    }

    /// <summary>Реальный IP — напрямую, мимо ядра; проверочного адреса нет или он не ответил — null.</summary>
    private async Task<string?> DirectIpAsync() => _options.Trace is { } trace
        ? await (_options.ExitIp ?? LatencyProbe.ExitIpAsync)(new ServerProbe(0, null, trace, DirectTimeout), CancellationToken.None).ConfigureAwait(false)
        : null;
}
