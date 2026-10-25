using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core;
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
    private TaskCompletionSource _nudge = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ReconnectTiming Timing => _options.Reconnect ?? ReconnectTiming.Default with { Every = null };

    /// <summary>«Защищено» — проверять связь с сервером, пока подключение то же.</summary>
    private void StartWatch(int session)
    {
        var watch = Interlocked.Increment(ref _watch);
        if (Timing.Every is { } every)
        {
            _ = WatchAsync(session, watch, every);
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

            if (OtherVpnStarted(session))
            {
                return;
            }

            // Одна неудача бывает и у живого сервера — вторая проба сразу.
            if (await ProbeAsync().ConfigureAwait(false) || await ProbeAsync().ConfigureAwait(false))
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
    /// мимо правил маршрутизации.
    /// </summary>
    private async Task<bool> ProbeAsync()
    {
        if (_probe is not { } link)
        {
            return false;
        }

        var probe = new ServerProbe(link.Port, new NetworkCredential(link.User, link.Password), _options.LatencyTarget, Timing.ProbeTimeout);
        return await (_options.Probe ?? LatencyProbe.ReachesAsync)(probe, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Сеть машины сменилась (другой Wi-Fi, точка доступа, пробуждение): «Защищено» — проверить сервер
    /// сразу, а не через минуту; «Переподключение» — попытка сразу, без остатка паузы.
    /// </summary>
    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // В другой сети и реальный IP другой: прежний замер не годится для проверки утечек.
        _directIp = null;
        if (Status.Exit.DirectIp is not null)
        {
            Update(s => s with { Exit = s.Exit with { DirectIp = null } });
        }

        var status = Status;
        var session = Volatile.Read(ref _session);
        if (status.State is ConnectionState.Connected or ConnectionState.Reconnecting && OtherVpnStarted(session))
        {
            return;
        }

        if (status is { State: ConnectionState.Connected, IsPaused: false } && Timing.Every is not null)
        {
            _journal.Add(JournalLevel.Info, "service", "Сеть машины сменилась — проверка связи с сервером");
            _ = CheckNowAsync(session, Volatile.Read(ref _watch));
        }
        else if (status.State == ConnectionState.Reconnecting)
        {
            _journal.Add(JournalLevel.Info, "service", "Сеть машины сменилась — попытка переподключения сразу");
            Volatile.Read(ref _nudge).TrySetResult();
        }
    }

    private async Task CheckNowAsync(int session, int watch)
    {
        try
        {
            if (!await ProbeAsync().ConfigureAwait(false) && !await ProbeAsync().ConfigureAwait(false) && watch == Volatile.Read(ref _watch))
            {
                await LostAsync(session, ErrorCodes.ServerNotResponding, "Сеть сменилась, сервер не отвечает").ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
            // Служба остановилась.
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

        _ = StopForOtherVpnAsync(session, other);
        return true;
    }

    /// <summary>Ядра останавливаются, блок при обрыве остается: трафик стоит, пока пользователь не решит.</summary>
    private async Task StopForOtherVpnAsync(int session, IReadOnlyList<string> other)
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

                _journal.Add(JournalLevel.Error, "service", $"{ErrorCodes.OtherVpn}: включился другой VPN ({string.Join(", ", other)}) — подключение остановлено"
                    + (_blocking ? ", блок при обрыве держит трафик" : string.Empty));
                Move(s => s with { ErrorCode = ErrorCodes.OtherVpn }, ConnectionState.Error);
                await StopCoreAsync().ConfigureAwait(false);
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

            if (!BlockWithoutTunnel())
            {
                Fail(session, ErrorCodes.KillSwitchFailed, "Блок при обрыве не обновился");
                return;
            }

            if (_request is not { Connection.Reconnect: not ReconnectPolicy.Never })
            {
                Fail(session, code, why + " — переподключение выключено");
                return;
            }

            _lostAt = _time.GetUtcNow();
            _journal.Add(JournalLevel.Warning, "service", $"{why} — переподключение к тому же серверу");
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
    }

    /// <summary>Попытки с растущей паузой, пока ядра не поднимутся и сервер не ответит или политика не скажет сдаться.</summary>
    private async Task ReconnectLoopAsync(int session, string code)
    {
        var timing = Timing;
        for (var attempt = 1; ; attempt++)
        {
            var nudge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _nudge, nudge);
            await Task.WhenAny(Task.Delay(Reconnect.Delay(attempt, timing.FirstPause, timing.MaxPause), _time), nudge.Task).ConfigureAwait(false);
            if (!IsReconnecting(session))
            {
                return;
            }

            _journal.Add(JournalLevel.Info, "service", $"Переподключение, попытка {attempt}");
            var (up, failure) = await RestartAsync(session).ConfigureAwait(false);
            if (up && (timing.Every is null || await ProbeAsync().ConfigureAwait(false)))
            {
                await RestoredAsync(session, attempt).ConfigureAwait(false);
                return;
            }

            code = up ? ErrorCodes.ServerNotResponding : failure;
            if (!IsReconnecting(session))
            {
                return;
            }

            if (Reconnect.GivesUp(_request!.Connection.Reconnect, _time.GetUtcNow() - _lostAt, timing.GiveUpAfter))
            {
                await GiveUpAsync(session, code).ConfigureAwait(false);
                return;
            }

            var next = attempt + 1;
            Update(s => s.State == ConnectionState.Reconnecting ? s with { Attempt = next } : s);
        }
    }

    /// <summary>
    /// Ядра заново с тем же запросом: старые останавливаются, новые поднимаются. Блок при обрыве стоит все
    /// это время; в режиме VPN он обновляется, когда появился новый адаптер туннеля. Адрес сервера — прежний:
    /// под блоком имя заново не найти.
    /// </summary>
    private async Task<(bool Up, string Failure)> RestartAsync(int session)
    {
        CoreRequest request;
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session) || _request is not { } current)
            {
                return (false, ErrorCodes.CoreFailed);
            }

            request = current;
            await StopCoreAsync().ConfigureAwait(false);
            if (!BlockWithoutTunnel())
            {
                return (false, ErrorCodes.KillSwitchFailed);
            }

            if (request.Mode == ConnectionMode.Proxy)
            {
                try
                {
                    var process = CoreProcess.StartXray(_options.XrayPath!, XrayConfigBuilder.Build(request, probe: _probe).Config!);
                    process.Output += (_, line) => _journal.Add(JournalLevel.Info, "xray", line);
                    process.Exited += (_, _) => OnCoreExited(session, process);
                    _core = process;
                    _journal.Add(JournalLevel.Info, "service", "Ядро Xray запущено, процесс " + process.Id);
                }
                catch (System.ComponentModel.Win32Exception e)
                {
                    _journal.Add(JournalLevel.Error, "service", "Ядро не запустилось: " + e.Message);
                    return (false, ErrorCodes.CoreFailed);
                }
            }
        }
        finally
        {
            _switch.Release();
        }

        var timeout = TimeSpan.FromSeconds(request.Connection.ConnectTimeoutSeconds);
        if (request.Mode == ConnectionMode.Proxy)
        {
            return (await OpensAsync(request.Connection.ProxyPort, timeout).ConfigureAwait(false), ErrorCodes.CoreFailed);
        }

        VpnTunnel tunnel;
        try
        {
            tunnel = await VpnTunnel.StartAsync(_options.XrayPath!, _options.SingBoxPath!, request, _addresses, timeout, _time, _options.TunnelNetns,
                (source, line) => _journal.Add(JournalLevel.Info, source, line), _probe, ServerIpv6(request, reuse: true)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TunnelException or System.ComponentModel.Win32Exception or IOException)
        {
            _journal.Add(JournalLevel.Error, "service", e.Message);
            return (false, ErrorCodes.CoreFailed);
        }

        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session))
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
                return (false, ErrorCodes.CoreFailed);
            }

            _tunnel = tunnel;
            tunnel.Exited += (_, _) => OnTunnelExited(session);
            _journal.Add(JournalLevel.Info, "service", $"Туннель поднят: Xray — процесс {tunnel.Processes.Xray}, sing-box — процесс {tunnel.Processes.SingBox}");
            if (tunnel.HasExited)
            {
                return (false, ErrorCodes.CoreFailed);
            }

            return _killSwitchOn && !Block(request, _addresses, "обновлен: адаптер туннеля открыт")
                ? (false, ErrorCodes.KillSwitchFailed)
                : (true, string.Empty);
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>Ядро открыло вход прокси за отведенное время.</summary>
    private async Task<bool> OpensAsync(int port, TimeSpan timeout)
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
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time).ConfigureAwait(false);
            }
        }

        return false;
    }

    private async Task RestoredAsync(int session, int attempt)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session))
            {
                return;
            }

            Move(s => s, ConnectionState.Connected);
            _journal.Add(JournalLevel.Info, "service", $"Связь с сервером восстановлена (попытка {attempt})");
            AfterConnected(session);
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
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsReconnecting(session))
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            BlockWithoutTunnel();
            if (_request is { Mode: ConnectionMode.Vpn } && _blocking)
            {
                Move(s => s, ConnectionState.Blocked);
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
