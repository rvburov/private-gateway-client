using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    // Сигнал меняется под _gate одновременно со снимком: восстановление между чтением и await не теряется.
    private TaskCompletionSource _reconnectChanged = NewNudge();
    private int _networkLoss;
    private readonly CancellationTokenSource _networkWaitStop = new();
    private static readonly TimeSpan OfflineRecheck = TimeSpan.FromSeconds(5);

    private void WakeReconnectWaiters()
    {
        lock (_gate)
        {
            var changed = _reconnectChanged;
            _reconnectChanged = NewNudge();
            changed.TrySetResult();
        }
    }

    /// <summary>Подтвержденный обрыв внешней сети: остановить старый запуск, сохранив намерение и защиту.</summary>
    private async Task SuspendForNetworkAsync(int session, bool observedLoss = false)
    {
        var startLoop = false;
        var entered = false;
        try
        {
            await _switch.WaitAsync(_networkWaitStop.Token).ConfigureAwait(false);
            entered = true;
            var status = Status;
            if (session != _session || status.IsPaused || (!status.NetworkUnavailable && !observedLoss)
                || status.State is not (ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting)
                || _request is null)
            {
                return;
            }

            // Предварительная проверка еще не приняла новое подключение: _request может относиться к старому.
            // Ее собственный таймаут остается ограниченным, а факт отсутствия сети уже виден в статусе.
            lock (_preflightGate)
            {
                if (_preflightDone is not null)
                {
                    return;
                }
            }

            if (status.State != ConnectionState.Reconnecting)
            {
                if (_request.Connection.Reconnect == ReconnectPolicy.Never)
                {
                    Fail(session, ErrorCodes.NetworkUnavailable, "Внешняя сеть отключена — переподключение выключено");
                    return;
                }

                _lostAt = _time.GetUtcNow();
                Volatile.Write(ref _nudge, NewNudge());
                Move(s => s, ConnectionState.Reconnecting);
                startLoop = true;
                _journal.Add(JournalLevel.Warning, "service", "Внешняя сеть отключена — ожидаем восстановления сети без новых попыток VPN");
            }

            // Сначала Reconnecting: ожидаемый выход старого ядра не должен превратиться в CoreFailed.
            await StopCoreAsync().ConfigureAwait(false);
            BlockWithoutTunnel();
            Update(s => Honest(s));
            if (!Status.NetworkUnavailable)
            {
                // Сеть вернулась, пока завершался старый запуск. Следующая попытка не ждет остатка backoff.
                Volatile.Read(ref _nudge).TrySetResult();
            }
        }
        catch (ObjectDisposedException)
        {
            // Остановка службы опередила обработчик события ОС.
        }
        catch (OperationCanceledException) when (_networkWaitStop.IsCancellationRequested)
        {
            // Остановка службы отменяет в том числе обработчик, ожидающий семафор.
        }
        finally
        {
            if (entered)
            {
                _switch.Release();
            }
        }

        if (startLoop)
        {
            _ = ReconnectAsync(session, ErrorCodes.NetworkUnavailable);
        }
    }

    /// <summary>Ожидание с отменой таймера; absolute due вызывающего не сдвигается от обновлений статуса.</summary>
    private async Task WaitForReconnectAsync(Task changed, Task? nudge, TimeSpan? timeout)
    {
        var lifetime = _networkWaitStop.Token;
        lifetime.ThrowIfCancellationRequested();
        if (timeout is null)
        {
            await changed.WaitAsync(lifetime).ConfigureAwait(false);
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var delay = Task.Delay(timeout.Value > TimeSpan.Zero ? timeout.Value : TimeSpan.Zero, _time, stop.Token);
        try
        {
            if (nudge is null)
            {
                await Task.WhenAny(changed, delay).ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(changed, nudge, delay).ConfigureAwait(false);
            }
            lifetime.ThrowIfCancellationRequested();
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Страховка от потерянного события ОС, например когда шлюз появился позже адреса адаптера.</summary>
    private void RefreshOfflineNetwork(int session)
    {
        var loss = Volatile.Read(ref _networkLoss);
        var network = ReadNetwork();
        if (network?.Kind == NetworkKind.None)
        {
            return;
        }

        lock (_gate)
        {
            if (session != _session || !_status.NetworkUnavailable || _status.IsPaused
                || _status.State != ConnectionState.Reconnecting || loss != Volatile.Read(ref _networkLoss))
            {
                return;
            }
            Update(s => s with
            {
                Network = network ?? s.Network,
                NetworkUnavailable = false,
                NetworkChangedAt = _time.GetUtcNow(),
            });
            Volatile.Read(ref _nudge).TrySetResult();
        }
    }
}
