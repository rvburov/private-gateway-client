using PrivateGateway.Client.Core;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    private CancellationTokenSource? _exitRefreshStop;
    private long _exitIpMeasurementVersion;

    /// <summary>Один замер IP на актуальное подключение/сеть. Явно выключенную автоматическую проверку не обходим.</summary>
    private Task StartExitIpRefreshAsync(int session)
    {
        lock (_gate)
        {
            if (_networkWaitStop.IsCancellationRequested || session != _session || _options.Trace is null
                || _request is not { Protection.CheckLeaksAfterConnect: true } request
                || _status is not { State: ConnectionState.Connected, IsPaused: false, NetworkUnavailable: false })
            {
                return Task.CompletedTask;
            }

            _exitRefreshStop?.Cancel();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(_networkWaitStop.Token);
            _exitRefreshStop = stop;
            return RefreshExitIpAsync(session, request, _exitIpMeasurementVersion, stop);
        }
    }

    private async Task RefreshExitIpAsync(int session, CoreRequest request, long measuredBefore, CancellationTokenSource stop)
    {
        var cancellationToken = stop.Token;
        var attempts = 0;
        var next = _time.GetUtcNow();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                ConnectionStatus status;
                bool measuredSinceStart;
                lock (_gate)
                {
                    if (session != _session || !ReferenceEquals(request, _request) || _status.State != ConnectionState.Connected)
                    {
                        return;
                    }

                    status = Honest(_status);
                    changed = _reconnectChanged.Task;
                    measuredSinceStart = _exitIpMeasurementVersion != measuredBefore;
                }

                if (status.IsPaused || status.NetworkUnavailable)
                {
                    await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (attempts > 0 && measuredSinceStart && status.Exit.Ip is not null && status.Protection.CheckedAt is not null)
                {
                    // Только новый ручной замер завершает повторы: IP и время прежней сети могли сохраниться.
                    return;
                }

                var delay = next - _time.GetUtcNow();
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        // Счетчики трафика будят ожидающего, но не сдвигают срок и не начинают лишних запросов.
                        await changed.WaitAsync(delay, _time, cancellationToken).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Наступил срок следующей попытки.
                    }

                    continue;
                }

                var result = await RefreshExitIpAttemptAsync(session, request, attempts == 1, cancellationToken).ConfigureAwait(false);
                if (result.State == CheckState.Ok || result.Reason == "same_ip")
                {
                    return;
                }

                attempts = Math.Min(attempts + 1, 5);
                next = _time.GetUtcNow().AddSeconds(Math.Min(30, 3 * (1 << (attempts - 1))));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Новое подключение/сеть или остановка службы отменили прежний замер.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_exitRefreshStop, stop))
                {
                    _exitRefreshStop = null;
                }
                stop.Dispose();
            }
        }
    }

    private async Task<(CheckState State, string? Reason)> RefreshExitIpAttemptAsync(int session, CoreRequest request,
        bool reportSilence, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task monitor;
        Task<(CheckState State, string? Reason)> probe;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session != _session || !ReferenceEquals(request, _request)
                || _status is not { State: ConnectionState.Connected, IsPaused: false, NetworkUnavailable: false })
            {
                return (CheckState.Warning, "session_changed");
            }

            // Начало пробы и ее наблюдателя атомарны относительно нового подключения и паузы.
            monitor = CancelExitIpAttemptOnChangeAsync(CaptureDiagnosticSnapshot(), _reconnectChanged.Task, attempt);
            probe = CheckExitAsync(reportSilence, attempt.Token);
        }
        try
        {
            return await DiagnosticFailure.RunAsync(_journal, "exit_ip_retry", () => probe,
                error => (State: CheckState.Warning, Reason: (string?)DiagnosticFailure.Reason(error)), attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Пауза/потеря сети отменяет только эту попытку. Общий цикл дождется возобновления.
            return (CheckState.Warning, "session_changed");
        }
        finally
        {
            attempt.Cancel();
            await monitor.ConfigureAwait(false);
        }
    }

    private async Task CancelExitIpAttemptOnChangeAsync(DiagnosticSnapshot snapshot, Task changed, CancellationTokenSource attempt)
    {
        try
        {
            while (true)
            {
                await changed.WaitAsync(attempt.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!IsDiagnosticSnapshotCurrent(snapshot))
                    {
                        attempt.Cancel();
                        return;
                    }
                    changed = _reconnectChanged.Task;
                }
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            // Попытка завершена или отменена; наблюдатель ей больше не нужен.
        }
    }
}
