using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Переподключение — только к выбранному серверу. Пока «Защищено», служба раз в
/// <see cref="ReconnectTiming.Every"/> проверяет, что сервер отвечает (запрос через ядро); две неудачи
/// подряд или выход ядра — «Переподключение»: ядра заново к тому же серверу с паузами 1, 2, 4… до 60 с,
/// блок при обрыве стоит все это время. Попытки кончились по политике — в режиме VPN с блоком
/// «Блокировка» (трафик стоит, другой сервер сам не выбирается), иначе «Ошибка».
/// </summary>
public sealed partial class Gateway
{
    // Когда пропала связь с сервером — для предела «До 5 минут».
    private DateTimeOffset _lostAt;

    // Номер проверки связи: новая проверка останавливает прежнюю.
    private int _watch;

    // Пауза перед попыткой переподключения: сменилась сеть — попытка сразу.
    private TaskCompletionSource _nudge = NewNudge();

    private ReconnectTiming Timing => _options.Reconnect ?? ReconnectTiming.Default with { Every = null };

    /// <summary>«Защищено» — проверять связь с сервером, пока подключение то же.</summary>
    private void StartWatch(int session)
    {
        var watch = Interlocked.Increment(ref _watch);
        if (Timing.Every is { } every)
        {
            _ = WatchAsync(session, watch, every);
        }

        if (_options.OtherClientsWatch is { } often)
        {
            _ = WatchOthersAsync(session, watch, often);
        }
    }

    /// <summary>
    /// Пока «Защищено» — другой VPN или прокси-клиент, включенный рядом (решение 525): проба сервера идет раз в минуту, а
    /// чужой туннель, который меняет одни маршруты, без нового адреса, смена сети не выдает. Таблица маршрутов и список
    /// процессов читаются быстро — раз в несколько секунд. Включился туннель — подключение остановлено; запустилось ядро —
    /// замечание в состоянии (окно — уведомлением).
    /// </summary>
    private async Task WatchOthersAsync(int session, int watch, TimeSpan often)
    {
        try
        {
            while (true)
            {
                await Task.Delay(often, _time).ConfigureAwait(false);
                ConnectionStatus status;
                lock (_gate)
                {
                    if (watch != _watch || session != _session)
                    {
                        return;
                    }

                    status = _status;
                }

                // Как и проверка связи: прежний наблюдатель заканчивается при обрыве, новый запускает AfterConnected.
                // Пауза сохраняет Connected, но поиска и уведомления на ней нет (решение 526).
                if (status.State != ConnectionState.Connected)
                {
                    return;
                }

                if (status.IsPaused)
                {
                    continue;
                }

                if (OtherVpnStarted(session))
                {
                    return;
                }

                NoteOtherClients(session);
            }
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
    }

    private async Task WatchAsync(int session, int watch, TimeSpan every)
    {
        try
        {
            await WatchLoopAsync(session, watch, every).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
    }

    private async Task WatchLoopAsync(int session, int watch, TimeSpan every)
    {
        while (true)
        {
            await Task.Delay(every, _time).ConfigureAwait(false);
            ConnectionStatus status;
            lock (_gate)
            {
                status = _status;
                if (watch != _watch || session != _session || status.State != ConnectionState.Connected)
                {
                    return;
                }
            }

            // На паузе для входа в Wi-Fi туннеля нет — и проверять нечего.
            if (status.IsPaused)
            {
                continue;
            }

            if (status.NetworkUnavailable)
            {
                await SuspendForNetworkAsync(session).ConfigureAwait(false);
                return;
            }

            if (OtherVpnStarted(session))
            {
                return;
            }

            NoteOtherClients(session);

            // Одна неудача бывает и у живого сервера — вторая проба сразу.
            if (await ProbeAsync(quiet: true).ConfigureAwait(false) || await ProbeAsync().ConfigureAwait(false))
            {
                continue;
            }

            if (watch == Volatile.Read(ref _watch))
            {
                await LostAsync(session, ErrorCodes.ServerNotResponding, "Сервер не отвечает").ConfigureAwait(false);
            }

            return;
        }
    }

    /// <summary>
    /// Отвечает ли сервер: запрос к проверочному адресу через вход проверки ядра — прямо в выход сервера,
    /// мимо правил маршрутизации; ждать не дольше <paramref name="timeout"/> (по умолчанию — <see cref="ReconnectTiming.ProbeTimeout"/>).
    /// <paramref name="quiet"/> — первая из двух проб: ее неудачу в журнал не писать. Одна неудача бывает и у живого сервера
    /// (у пользователя на раздаче iPhone первая проба раз в минуту обрывалась на TLS, вторая проходила), и предупреждение
    /// каждую минуту забивало журнал, хотя защита работала; настоящую неудачу запишет вторая проба.
    /// </summary>
    private async Task<bool> ProbeAsync(TimeSpan? timeout = null, bool quiet = false)
    {
        if (Status.NetworkUnavailable || _probe is not { } link)
        {
            return false;
        }

        CancellationToken cancellation;
        try
        {
            cancellation = _starting.Token;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        var probe = new ServerProbe(link.Port, new NetworkCredential(link.User, link.Password), _options.LatencyTarget, timeout ?? Timing.ProbeTimeout);
        try
        {
            if (_options.Probe is { } custom)
            {
                return await custom(probe, cancellation).WaitAsync(cancellation).ConfigureAwait(false);
            }

            var result = await LatencyProbe.CheckAsync(probe, cancellation).ConfigureAwait(false);
            if (!result.Reached && !quiet)
            {
                _journal.Add(JournalLevel.Warning, "service", "Проверка связи через ядро: " + result.Description);
            }

            return result.Reached;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Сеть машины сменилась (другой Wi-Fi, точка доступа, пробуждение): «Защищено» — проверить сервер
    /// сразу, а не через минуту; «Переподключение» — попытка сразу, без остатка паузы.
    /// </summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // В другой сети и реальный IP другой: прежний замер не годится для проверки утечек. Время смены окно берет для
        // уведомления «Нужен вход в Wi-Fi».
        (_directIp, _directIp6, _directWebrtc) = (null, null, null);
        var now = _time.GetUtcNow();
        var network = ReadNetwork();
        if (network?.Kind == NetworkKind.None)
        {
            Interlocked.Increment(ref _networkLoss);
        }
        Update(s => s with
        {
            Exit = s.Exit with { DirectIp = null },
            NetworkChangedAt = now,
            Network = network ?? s.Network,
            NetworkUnavailable = network?.Kind == NetworkKind.None,
        });
        if (network?.Kind == NetworkKind.None)
        {
            _ = SuspendForNetworkAsync(Volatile.Read(ref _session), observedLoss: true);
            return;
        }

        // Другая основная сеть (macOS): ее DNS — тоже на туннель, прежней — свой.
        if (Volatile.Read(ref _tunnel) is { } tunnel)
        {
            PointDnsAtTunnel(tunnel);
        }

        CheckAtOnce("Сеть машины сменилась");
    }

    /// <summary>
    /// Машина проснулась: таймеры .NET во сне стояли. Пауза для входа в Wi-Fi, чей срок по часам вышел во сне, кончается сейчас,
    /// а не через остаток своего таймера; иначе — как после смены сети: сервер проверить сразу, попытка сразу.
    /// </summary>
    private void OnResumed(object? sender, TimeSpan slept)
    {
        _journal.Add(JournalLevel.Info, "service", $"Машина проснулась после сна ({slept.TotalMinutes:0.#} мин)");
        if (Status.CaptivePauseUntil is { } until && _time.GetUtcNow() >= until)
        {
            _ = ResumeAsync(Volatile.Read(ref _session), "время вышло во сне");
            return;
        }

        CheckAtOnce("Машина проснулась");
    }

    /// <summary>«Защищено» — проверить сервер сразу, а не через минуту; «Переподключение» — попытка сразу, без остатка паузы.</summary>
    private void CheckAtOnce(string why)
    {
        var status = Status;
        var session = Volatile.Read(ref _session);
        if (status.NetworkUnavailable)
        {
            _ = SuspendForNetworkAsync(session);
            return;
        }
        if (status.State is ConnectionState.Connected or ConnectionState.Reconnecting && OtherVpnStarted(session))
        {
            return;
        }

        if (status is { State: ConnectionState.Connected, IsPaused: false } && Timing.Every is not null)
        {
            _journal.Add(JournalLevel.Info, "service", $"{why} — проверка связи с сервером");
            _ = CheckNowAsync(session, Volatile.Read(ref _watch));
        }
        else if (status is { State: ConnectionState.Reconnecting, IsPaused: false })
        {
            // На паузе для входа в Wi-Fi попыток нет — подгонять нечего.
            _journal.Add(JournalLevel.Info, "service", $"{why} — попытка переподключения сразу");
            Volatile.Read(ref _nudge).TrySetResult();
        }
    }

    private async Task CheckNowAsync(int session, int watch)
    {
        try
        {
            if (!await ProbeAsync(quiet: true).ConfigureAwait(false) && !await ProbeAsync().ConfigureAwait(false) && watch == Volatile.Read(ref _watch))
            {
                await LostAsync(session, ErrorCodes.ServerNotResponding, "Сеть сменилась, сервер не отвечает").ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
        catch (OperationCanceledException) when (_networkWaitStop.IsCancellationRequested)
        {
            // Ожидание сети или пауза перед повтором отменены остановкой службы.
        }
    }

    /// <summary>
    /// Во время подключения включился чужой VPN с маршрутом ко всему интернету: трафик может пойти мимо
    /// туннеля, а окно говорило бы «Защищено». Пользователь его не разрешал — подключение останавливается
    /// с PGC-NET-010, как при подключении. Проверка — при смене сети и с каждой пробой: выход через VPN,
    /// у которого адаптер уже был (Tailscale), включается одними маршрутами, без смены адресов.
    /// </summary>
    private bool OtherVpnStarted(int session)
    {
        if (Status is not { Mode: ConnectionMode.Vpn, IsPaused: false } || _allowOtherVpn || OtherVpnAdapters() is not { Count: > 0 } other)
        {
            return false;
        }

        _ = StopForOtherVpnAsync(session, "включился другой VPN: туннель с маршрутом ко всему интернету",
            new OtherClientList([.. other.Select(adapter => new OtherClient(OtherClientSign.Adapter, adapter))]));
        return true;
    }

    /// <summary>
    /// Во время подключения запустилась другая программа VPN или прокси (работает ее ядро): окно говорит об этом уведомлением
    /// (<see cref="ConnectionStatus.OtherClients"/> в «Защищено»), подключение идет дальше — чужой туннель с маршрутом ко всему
    /// интернету останавливает его сам (<see cref="OtherVpnStarted"/>). Пользователь разрешил работу рядом с другим VPN —
    /// молчать. Проверка — с каждой пробой связи.
    /// </summary>
    private void NoteOtherClients(int session)
    {
        if (_allowOtherVpn || _options.Processes is null)
        {
            return;
        }

        var found = OtherClientsNow(vpn: false, foreignProxy: null);
        var list = found.Count == 0 ? null : new OtherClientList(found);
        lock (_gate)
        {
            if (session != _session || _status.State != ConnectionState.Connected || _status.IsPaused || Equals(_status.OtherClients, list))
            {
                return;
            }

            _status = _status with { OtherClients = list };
            foreach (var watcher in _watchers)
            {
                watcher.TryWrite(_status);
            }
        }

        _journal.Add(list is null ? JournalLevel.Info : JournalLevel.Warning, "service", list is null
            ? "Другой VPN или прокси-клиент больше не работает"
            : $"Запустился другой VPN или прокси-клиент ({Describe(found)}) — трафик может пойти мимо туннеля");
    }

    /// <summary>
    /// Окно заметило во время подключения системный прокси другой программы (служба системный прокси пользователя не видит,
    /// решение 511): его вход на этой машине слушает не свое ядро — программы, которые берут системный прокси, идут мимо
    /// туннеля через нее, а в режиме прокси она еще и заменила свой прокси. Подключение останавливается с PGC-NET-010 и ее
    /// названием, как при подключении; блок при обрыве остается. Разрешена работа рядом с другим VPN, вход не слушает,
    /// подключения нет — ничего. Остановлено — true.
    /// </summary>
    public bool ForeignProxyAppeared(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int session;
        lock (_gate)
        {
            if (_allowOtherVpn || _status.State is not (ConnectionState.Connected or ConnectionState.Reconnecting) || _status.IsPaused)
            {
                return false;
            }

            session = _session;
        }

        var found = OtherClientsNow(vpn: false, foreignProxy: address);
        if (!found.Any(c => c.Sign == OtherClientSign.SystemProxy))
        {
            return false;
        }

        _ = StopForOtherVpnAsync(session, "включился другой VPN или прокси-клиент (" + Describe(found) + ")", new OtherClientList(found));
        return true;
    }

    /// <summary>
    /// Ядра останавливаются, блок при обрыве остается: трафик стоит, пока пользователь не решит. <paramref name="what"/> — что
    /// включилось, для журнала; <paramref name="clients"/> — что назвать на экране «Работает другой VPN или прокси-клиент».
    /// </summary>
    private async Task StopForOtherVpnAsync(int session, string what, OtherClientList? clients = null)
    {
        try
        {
            await _switch.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (session != _session || _status.State is not (ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting))
                    {
                        return;
                    }
                }

                _journal.Add(JournalLevel.Error, "service", $"{ErrorCodes.OtherVpn}: {what} — подключение остановлено"
                    + (_blocking ? ", блок при обрыве держит трафик" : string.Empty));
                Move(s => s with { ErrorCode = ErrorCodes.OtherVpn, OtherClients = clients ?? s.OtherClients }, ConnectionState.Error);
                await StopCoreAsync().ConfigureAwait(false);
                BlockWithoutTunnel();
            }
            finally
            {
                _switch.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
    }

    private void OnTunnelExited(int session)
    {
        if (Status.State == ConnectionState.Connected)
        {
            _ = LostAsync(session, ErrorCodes.CoreFailed, "Ядро туннеля остановилось");
            return;
        }

        Fail(session, ErrorCodes.CoreFailed, "Ядро туннеля остановилось");
    }

    /// <summary>
    /// Связь с сервером пропала — ядро вышло или сервер не отвечает. Переподключение — к тому же серверу;
    /// «Не повторять» — сразу «Ошибка». Блок при обрыве остается: трафик на паузе, а не напрямую.
    /// </summary>
    private async Task LostAsync(int session, string code, string why)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (session != _session || _status.State != ConnectionState.Connected || _status.IsPaused)
                {
                    return;
                }
            }

            // Не обновился — прежний блок остается, а каждая попытка переподключения ставит его заново.
            BlockWithoutTunnel();
            if (_request is not { Connection.Reconnect: not ReconnectPolicy.Never })
            {
                Fail(session, code, why + " — переподключение выключено");
                return;
            }

            _lostAt = _time.GetUtcNow();
            _journal.Add(JournalLevel.Warning, "service", $"{why} — переподключение к тому же серверу");
            Volatile.Write(ref _nudge, NewNudge());
            Move(s => s, ConnectionState.Reconnecting);
        }
        finally
        {
            _switch.Release();
        }

        _ = ReconnectAsync(session, code);
    }

    private async Task ReconnectAsync(int session, string code)
    {
        try
        {
            await ReconnectLoopAsync(session, code).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
        }
        catch (OperationCanceledException) when (_networkWaitStop.IsCancellationRequested)
        {
            // Ожидание внешней сети завершено вместе со службой.
        }
    }

    /// <summary>
    /// Сигнал «попытка сразу» (смена сети, пробуждение): свежий — с началом переподключения, до «Переподключения», чтобы
    /// смена сети сразу после него не пропала; остаток прошлого переподключения первую паузу не съедает.
    /// </summary>
    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Попытки с растущей паузой, пока ядра не поднимутся и сервер не ответит или политика не скажет сдаться.</summary>
    private async Task ReconnectLoopAsync(int session, string code)
    {
        var timing = Timing;
        var attempt = 1;
        var due = _time.GetUtcNow() + Reconnect.Delay(attempt, timing.FirstPause, timing.MaxPause);
        while (true)
        {
            ConnectionStatus status;
            CoreRequest? request;
            Task changed;
            lock (_gate)
            {
                status = _status;
                request = _request;
                changed = _reconnectChanged.Task;
                if (session != _session || status.State != ConnectionState.Reconnecting || status.IsPaused || request is null)
                {
                    return;
                }
            }

            var now = _time.GetUtcNow();
            var remaining = timing.GiveUpAfter - (now - _lostAt);
            if (Reconnect.GivesUp(request.Connection.Reconnect, now - _lostAt, timing.GiveUpAfter))
            {
                await GiveUpAsync(session, status.NetworkUnavailable ? ErrorCodes.NetworkUnavailable : code).ConfigureAwait(false);
                return;
            }

            TimeSpan? untilLimit = request.Connection.Reconnect == ReconnectPolicy.UpTo5Minutes ? remaining : null;
            if (status.NetworkUnavailable)
            {
                code = ErrorCodes.NetworkUnavailable;
                await WaitForReconnectAsync(changed, null,
                    untilLimit is { } limit && limit < OfflineRecheck ? limit : OfflineRecheck).ConfigureAwait(false);
                RefreshOfflineNetwork(session);
                continue;
            }

            var nudge = Volatile.Read(ref _nudge);
            var pause = due - now;
            if (pause > TimeSpan.Zero && !nudge.Task.IsCompleted)
            {
                await WaitForReconnectAsync(changed, nudge.Task,
                    untilLimit is { } limit && limit < pause ? limit : pause).ConfigureAwait(false);
                continue;
            }

            // Подгон забран. Смена сети во время попытки подгоняет следующую: ее сигнал ждет конца попытки, а не пропадает.
            Interlocked.CompareExchange(ref _nudge, NewNudge(), nudge);
            if (!IsReconnecting(session) || Status.NetworkUnavailable)
            {
                continue;
            }

            var networkLoss = Volatile.Read(ref _networkLoss);
            var (up, failure) = await RestartAsync(session, attempt).ConfigureAwait(false);
            if (up && !Status.NetworkUnavailable && (timing.Every is null || await ProbeAsync().ConfigureAwait(false))
                && await RestoredAsync(session, attempt, networkLoss).ConfigureAwait(false))
            {
                return;
            }

            code = networkLoss != Volatile.Read(ref _networkLoss) ? ErrorCodes.NetworkUnavailable
                : up ? ErrorCodes.ServerNotResponding : failure.Length > 0 ? failure : code;
            if (!IsReconnecting(session))
            {
                return;
            }

            attempt++;
            due = _time.GetUtcNow() + Reconnect.Delay(attempt, timing.FirstPause, timing.MaxPause);
        }
    }

    /// <summary>
    /// Ядра заново с тем же запросом: старые останавливаются, новые поднимаются. Блок при обрыве стоит все
    /// это время; в режиме VPN он обновляется, когда появился новый адаптер туннеля. Перебираются только ранее
    /// разрешенные адреса того же профиля: под блоком имя заново не ищется и набор исключений не расширяется.
    /// </summary>
    private async Task<(bool Up, string Failure)> RestartAsync(int session, int attempt)
    {
        CoreRequest request;
        CancellationToken starting;
        Task<VpnTunnel>? pending = null;
        Func<bool> relayReady = static () => true;
        await _switch.WaitAsync(_networkWaitStop.Token).ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session) || Status.NetworkUnavailable || _request is not { } current)
            {
                return (false, ErrorCodes.CoreFailed);
            }

            // Под замком: пауза меняет источник отмены под ним же.
            request = current;
            if (Reconnect.GivesUp(request.Connection.Reconnect, _time.GetUtcNow() - _lostAt, Timing.GiveUpAfter))
            {
                return (false, string.Empty);
            }
            await StopCoreAsync().ConfigureAwait(false);
            if (!IsReconnecting(session) || Status.NetworkUnavailable)
            {
                return (false, ErrorCodes.NetworkUnavailable);
            }
            if (Reconnect.GivesUp(request.Connection.Reconnect, _time.GetUtcNow() - _lostAt, Timing.GiveUpAfter))
            {
                return (false, string.Empty);
            }
            starting = _starting.Token;
            if (request.Mode == ConnectionMode.Vpn && _addresses.Count > 1)
            {
                // Только уже разрешенные адреса того же профиля: firewall держит тот же набор конечных точек,
                // а нерабочий первый адрес не закрепляет каждую попытку навсегда за одним путем.
                _addresses = [.. _addresses.Skip(1), _addresses[0]];
                _journal.Add(JournalLevel.Info, "service", "Переподключение: следующий из ранее разрешенных адресов того же сервера");
            }
            if (!BlockWithoutTunnel())
            {
                return (false, ErrorCodes.KillSwitchFailed);
            }

            Update(s => s.State == ConnectionState.Reconnecting ? s with { Attempt = attempt } : s);
            _journal.Add(JournalLevel.Info, "service", $"Переподключение, попытка {attempt}");

            if (request.Mode == ConnectionMode.Proxy)
            {
                try
                {
                    relayReady = StartProxyCore(session, request);
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or TunnelException)
                {
                    _journal.Add(JournalLevel.Error, "service", "Ядро не запустилось: " + e.Message);
                    return (false, ErrorCodes.CoreFailed);
                }
            }
            else
            {
                pending = BeginTunnelStart(request, _addresses, reuseIpv6: true, starting);
            }
        }
        finally
        {
            _switch.Release();
        }

        var timeout = TimeSpan.FromSeconds(request.Connection.ConnectTimeoutSeconds);
        if (request.Mode == ConnectionMode.Proxy)
        {
            return (await OpensAsync(request.Connection.ProxyPort, relayReady, timeout).ConfigureAwait(false), ErrorCodes.CoreFailed);
        }

        VpnTunnel tunnel;
        try
        {
            tunnel = await pending!.ConfigureAwait(false);
        }
        catch (Exception e) when (e is TunnelException or System.ComponentModel.Win32Exception or IOException)
        {
            _journal.Add(JournalLevel.Error, "service", e.Message);
            return (false, ErrorCodes.CoreFailed);
        }
        catch (OperationCanceledException) when (starting.IsCancellationRequested)
        {
            // Пауза для входа в Wi-Fi: попытки встали до ее конца.
            return (false, ErrorCodes.CoreFailed);
        }

        try
        {
            await _switch.WaitAsync(starting).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (starting.IsCancellationRequested)
        {
            return (false, ErrorCodes.CoreFailed);
        }
        try
        {
            if (!ReferenceEquals(_pendingTunnel, pending))
            {
                return (false, ErrorCodes.CoreFailed);
            }
            _pendingTunnel = null;
            bool publish;
            lock (_gate)
            {
                publish = session == _session && _status.State == ConnectionState.Reconnecting && !_status.NetworkUnavailable;
                if (publish)
                {
                    _tunnel = tunnel;
                }
            }
            if (!publish)
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
                return (false, ErrorCodes.CoreFailed);
            }

            tunnel.Exited += (_, _) => OnTunnelExited(session);
            _journal.Add(JournalLevel.Info, "service", $"Туннель поднят: Xray — процесс {tunnel.Processes.Xray}, sing-box — процесс {tunnel.Processes.SingBox}");
            if (tunnel.Relay is { } relay)
            {
                _journal.Add(JournalLevel.Info, "service", "Ретранслятор sing-box для Hysteria2 и TUIC — процесс " + relay);
            }
            PointDnsAtTunnel(tunnel);
            if (tunnel.HasExited)
            {
                return (false, ErrorCodes.CoreFailed);
            }

            return !Block(request, _addresses, _killSwitchOn ? "обновлен: адаптер туннеля открыт" : "включены")
                ? (false, ErrorCodes.KillSwitchFailed)
                : (true, string.Empty);
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>Ядро открыло вход прокси, а ретранслятор Hysteria2 и TUIC, если он есть, — свои (<paramref name="relayReady"/>), за отведенное время.</summary>
    private async Task<bool> OpensAsync(int port, Func<bool> relayReady, TimeSpan timeout)
    {
        var started = _time.GetTimestamp();
        while (_time.GetElapsedTime(started) < timeout)
        {
            if (Volatile.Read(ref _core) is not { HasExited: false })
            {
                return false;
            }

            try
            {
                if (relayReady())
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                    return true;
                }
            }
            catch (SocketException)
            {
                // Вход еще не открыт.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), _time).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> RestoredAsync(int session, int attempt, int networkLoss)
    {
        await _switch.WaitAsync(_networkWaitStop.Token).ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session) || Status.NetworkUnavailable || networkLoss != Volatile.Read(ref _networkLoss))
            {
                return false;
            }

            // Переподключение после подключения, на которое сервер не ответил, системного прокси еще не ставило.
            var proxyWasActive = _systemProxy?.IsActive == true;
            ApplySystemProxy();
            lock (_gate)
            {
                if (!_status.NetworkUnavailable && networkLoss == Volatile.Read(ref _networkLoss))
                {
                    _journal.Add(JournalLevel.Info, "service", $"Связь с сервером восстановлена (попытка {attempt})");
                    Move(s => s, ConnectionState.Connected);
                    AfterConnected(session);
                    return true;
                }
            }
            if (!proxyWasActive)
            {
                _systemProxy?.Release();
            }
            return false;
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>
    /// Попытки кончились: ядра останавливаются. В режиме VPN с блоком — «Блокировка»: сервер не отвечает,
    /// блок держит трафик, другой сервер сам не выбирается; иначе — «Ошибка» с кодом последней неудачи.
    /// </summary>
    private async Task GiveUpAsync(int session, string code)
    {
        await _switch.WaitAsync(_networkWaitStop.Token).ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            if (!BlockWithoutTunnel())
            {
                // Прежний блок остается — с разрешением адаптеру, которого уже нет: «Блокировка» была бы неправдой.
                code = ErrorCodes.KillSwitchFailed;
            }
            else if (_request is { Mode: ConnectionMode.Vpn } && _blocking)
            {
                Move(s => s, ConnectionState.Blocked,
                    code == ErrorCodes.NetworkUnavailable ? s => s with { ErrorCode = code } : null);
                _journal.Add(JournalLevel.Warning, "service", "Сервер не отвечает, попытки кончились: блок при обрыве держит трафик, другой сервер сам не выбирается");
                return;
            }

            Move(s => s with { ErrorCode = code }, ConnectionState.Error);
            _journal.Add(JournalLevel.Error, "service", $"{code}: связь с сервером не вернулась, попытки кончились");
        }
        finally
        {
            _switch.Release();
        }
    }

    private bool IsReconnecting(int session)
    {
        lock (_gate)
        {
            return session == _session && _status.State == ConnectionState.Reconnecting;
        }
    }
}
