using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Что умеет эта служба. <paramref name="CanVpn"/> — запущена с правами администратора;
/// <paramref name="LatencyTarget"/> — проверочный адрес задержки (в тестах — свой сайт).
/// </summary>
public sealed record GatewayOptions(string? XrayPath, bool CanVpn, Uri LatencyTarget);

/// <summary>
/// Защита на стороне службы: запрос Connect — конфиг ядра собирается здесь же из описания сервера
/// и настроек, ядро запускается отдельным процессом, состояние меняется только по
/// <see cref="ConnectionStateMachine"/> и уходит подписчикам WatchStatus. Сейчас — режим прокси;
/// режим VPN (TUN, брандмауэр) — следующие шаги этапа 3.
/// </summary>
public sealed class Gateway : IAsyncDisposable
{
    private static readonly TimeSpan LatencyTimeout = TimeSpan.FromSeconds(5);

    private readonly GatewayOptions _options;
    private readonly TimeProvider _time;
    private readonly ServiceJournal _journal;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly Lock _gate = new();
    private ConnectionStatus _status;
    private XrayProcess? _core;
    private readonly List<ChannelWriter<ConnectionStatus>> _watchers = [];

    // Номер подключения: выход старого ядра и запоздалая готовность не трогают новое.
    private int _session;

    public Gateway(GatewayOptions options, TimeProvider time, ServiceJournal journal)
    {
        _options = options;
        _time = time;
        _journal = journal;
        _status = ConnectionStatus.Initial(time.GetUtcNow());
    }

    public ConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Текущее состояние и все следующие — атомарно: снимок и подписка под той же блокировкой, что
    /// и смена состояния, поэтому изменение не теряется и не приходит дважды. Подписка снимается
    /// завершением <paramref name="stop"/>.
    /// </summary>
    public (ConnectionStatus Current, ChannelReader<ConnectionStatus> Updates) Watch(CancellationToken stop)
    {
        var channel = Channel.CreateUnbounded<ConnectionStatus>(new UnboundedChannelOptions { SingleReader = true });
        ConnectionStatus current;
        lock (_gate)
        {
            _watchers.Add(channel.Writer);
            current = _status;
        }

        stop.Register(() =>
        {
            lock (_gate)
            {
                _watchers.Remove(channel.Writer);
            }

            channel.Writer.TryComplete();
        });
        return (current, channel.Reader);
    }

    public async Task<V1.ConnectReply> ConnectAsync(V1.ConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CoreRequest core;
        try
        {
            var (protection, _) = (request.Protection ?? throw new ArgumentException("Нет настроек защиты")).ToCore();
            core = new CoreRequest(
                (request.Server ?? throw new ArgumentException("Нет сервера")).ToCore(),
                request.Mode.ToCore(),
                protection,
                (request.Connection ?? throw new ArgumentException("Нет настроек подключения")).ToCore(),
                (request.Routing ?? throw new ArgumentException("Нет маршрутизации")).ToCore());
        }
        catch (ArgumentException e)
        {
            return Reject(ErrorCodes.BadRequest, e.Message);
        }

        if (core.Mode == ConnectionMode.Vpn)
        {
            return Reject(_options.CanVpn ? ErrorCodes.VpnNotReady : ErrorCodes.AdminRequired, "Режим VPN недоступен");
        }

        var built = XrayConfigBuilder.Build(core);
        if (built.Config is not { } config)
        {
            return Reject(ErrorCodes.UnsupportedServer, "Ядро Xray не возьмется за сервер: " + built.Rejection);
        }

        if (_options.XrayPath is not { } xray || !File.Exists(xray))
        {
            return Reject(ErrorCodes.CoreFailed, "Нет сборки ядра Xray");
        }

        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        int session;
        try
        {
            // Сначала новый номер: выход старого ядра при остановке не должен стать ошибкой нового подключения.
            session = ++_session;
            await StopCoreAsync().ConfigureAwait(false);
            var credentials = core.Server.Credentials;
            _journal.Hide([credentials.Uuid, credentials.Password, credentials.ObfsPassword, credentials.PrivateKey, credentials.PresharedKey]);
            Move(s => s with { Mode = core.Mode, ServerId = request.Server.Id }, ConnectionState.Connecting);
            _journal.Add(JournalLevel.Info, "service", $"Подключение в режиме прокси: {core.Server.Protocol}, порт {core.Connection.ProxyPort}");
            try
            {
                var process = XrayProcess.Start(xray, config);
                process.Output += (_, line) => _journal.Add(JournalLevel.Info, "xray", line);
                process.Exited += (_, _) => OnCoreExited(session, process);
                _core = process;
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Fail(session, ErrorCodes.CoreFailed, "Ядро не запустилось: " + e.Message);
                return new V1.ConnectReply { Accepted = true };
            }
        }
        finally
        {
            _switch.Release();
        }

        _ = WaitReadyAsync(session, core.Connection.ProxyPort, TimeSpan.FromSeconds(core.Connection.ConnectTimeoutSeconds));
        return new V1.ConnectReply { Accepted = true };
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session++;
            await StopCoreAsync().ConfigureAwait(false);
            _journal.Hide([]);
            if (Status.State != ConnectionState.Off)
            {
                Move(s => s, ConnectionState.Off);
                _journal.Add(JournalLevel.Info, "service", "Защита выключена");
            }
        }
        finally
        {
            _switch.Release();
        }
    }

    /// <summary>Задержка через каждый сервер — запросом через ядро (<see cref="LatencyProbe"/>).</summary>
    public async Task<V1.MeasureLatencyReply> MeasureLatencyAsync(V1.MeasureLatencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = new V1.MeasureLatencyReply();
        var servers = new List<ServerDefinition>();
        var ids = new List<string>();
        foreach (var target in request.Targets)
        {
            try
            {
                servers.Add((target.Server ?? throw new ArgumentException("Нет сервера")).ToCore());
                ids.Add(target.Id);
            }
            catch (ArgumentException)
            {
                reply.Results.Add(LatencyResult.Failed(LatencyFailure.Unsupported).ToContract(target.Id));
            }
        }

        if (servers.Count == 0)
        {
            return reply;
        }

        IReadOnlyList<LatencyResult> results = _options.XrayPath is { } xray && File.Exists(xray)
            ? await LatencyProbe.MeasureAsync(xray, servers, ConnectionSettings.Default, _options.LatencyTarget, LatencyTimeout,
                _time, cancellationToken).ConfigureAwait(false)
            : [.. servers.Select(_ => LatencyResult.Failed(LatencyFailure.CoreFailed))];
        reply.Results.AddRange(results.Select((r, i) => r.ToContract(ids[i])));
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync().ConfigureAwait(false);
        _switch.Dispose();
    }

    private V1.ConnectReply Reject(string code, string message)
    {
        _journal.Add(JournalLevel.Warning, "service", $"Подключение отклонено ({code}): {message}");
        return new V1.ConnectReply { Accepted = false, Error = new V1.ErrorInfo { Code = code, Message = message } };
    }

    /// <summary>Готово, когда ядро открыло вход прокси; не открыло за время ожидания — ошибка ядра.</summary>
    private async Task WaitReadyAsync(int session, int port, TimeSpan timeout)
    {
        var started = _time.GetTimestamp();
        while (session == Volatile.Read(ref _session) && _time.GetElapsedTime(started) < timeout)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
                Advance(session, ConnectionState.Connected);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), _time).ConfigureAwait(false);
            }
        }

        Fail(session, ErrorCodes.CoreFailed, "Ядро не открыло вход прокси за " + timeout.TotalSeconds + " с");
    }

    private void OnCoreExited(int session, XrayProcess process)
    {
        var code = process.HasExited ? process.ExitCode : 0;
        Fail(session, ErrorCodes.CoreFailed, "Ядро остановилось, код " + code);
    }

    private void Advance(int session, ConnectionState state)
    {
        lock (_gate)
        {
            if (session != _session || _status.State != ConnectionState.Connecting)
            {
                return;
            }
        }

        Move(s => s, state);
        _journal.Add(JournalLevel.Info, "service", "Подключено: прокси на 127.0.0.1 готов");
    }

    /// <summary>Ошибка текущего подключения; ядро старого подключения в состояние не вмешивается.</summary>
    private void Fail(int session, string code, string message)
    {
        lock (_gate)
        {
            if (session != _session || _status.State is not (ConnectionState.Connecting or ConnectionState.Connected))
            {
                return;
            }
        }

        _journal.Add(JournalLevel.Error, "service", $"{code}: {message}");
        Move(s => s with { ErrorCode = code }, ConnectionState.Error);
        _ = StopFailedAsync(session);
    }

    /// <summary>Ядро, не открывшее вход вовремя, может еще работать — его останавливает служба.</summary>
    private async Task StopFailedAsync(int session)
    {
        await _switch.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session == _session)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _switch.Release();
        }
    }

    private void Move(Func<ConnectionStatus, ConnectionStatus> change, ConnectionState state)
    {
        lock (_gate)
        {
            _status = change(_status).MoveTo(state, _time.GetUtcNow());
            foreach (var watcher in _watchers)
            {
                watcher.TryWrite(_status);
            }
        }
    }

    private async Task StopCoreAsync()
    {
        if (Interlocked.Exchange(ref _core, null) is { } core)
        {
            await core.StopAsync().ConfigureAwait(false);
            core.Dispose();
        }
    }
}
