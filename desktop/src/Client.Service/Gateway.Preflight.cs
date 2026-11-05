using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    private readonly Lock _preflightGate = new();
    private CancellationTokenSource? _preflightStop;
    private Task? _preflightDone;

    private sealed record PreflightDecision(V1.ConnectReply? Reply, IReadOnlyList<IPAddress> Addresses, bool UseOutsideBinding = true);

    // Вызывается новым намерением подключения, отключением и остановкой службы. В отличие от TUN, временное ядро
    // проверки никогда не меняет ОС; его finally снимает процесс даже при прерванном ожидании RPC.
    private void CancelPreflight()
    {
        lock (_preflightGate)
        {
            _preflightStop?.Cancel();
        }
    }

    private async Task DrainPreflightAsync()
    {
        Task? pending;
        Task? cancellation;
        lock (_preflightGate)
        {
            cancellation = _preflightStop?.CancelAsync();
            pending = _preflightDone;
        }

        if (cancellation is not null)
        {
            await cancellation.ConfigureAwait(false);
        }

        if (pending is not null)
        {
            // Это только ядро и его finally, без RejectAsync и без публикации состояния под _switch.
            await pending.ConfigureAwait(false);
        }
    }

    private async Task<PreflightDecision> TryPreflightAsync(V1.ConnectRequest request, CoreRequest core,
        IReadOnlyList<IPAddress> addresses, int generation)
    {
        // В режиме с выключенными пробами сохраняется прежний контракт. Уже установленный блок или работающий VPN
        // не открываются ради проверки: с ними действует обычная ограниченная попытка подключения под защитой.
        if (core.Mode != ConnectionMode.Vpn || (Timing.Every is null && _options.Preflight is null))
        {
            return new(null, addresses);
        }

        using var stop = new CancellationTokenSource();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _switch.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!IsLatestConnect(generation))
                {
                    return new(SupersededConnect(), addresses);
                }

                var status = Status;
                if (_blocking || _core is not null || _tunnel is not null || status.IsPaused
                    || status.State is not (ConnectionState.Off or ConnectionState.Error or ConnectionState.Connecting))
                {
                    return new(null, addresses);
                }

                await DrainPreflightAsync().ConfigureAwait(false);
                if (!IsLatestConnect(generation))
                {
                    return new(SupersededConnect(), addresses);
                }

                if (Volatile.Read(ref _pendingTunnel) is not null)
                {
                    // Прежний запуск без блока еще не опубликовал TUN. Его нужно завершить до свежей проверки,
                    // иначе он мог бы создать адаптер и перехватить ее трафик посреди проверки нового профиля.
                    _session++;
                    await StopCoreAsync().ConfigureAwait(false);
                    if (!IsLatestConnect(generation))
                    {
                        return new(SupersededConnect(), addresses);
                    }
                }

                lock (_preflightGate)
                {
                    _preflightStop = stop;
                    _preflightDone = completed.Task;
                }

                if (!IsLatestConnect(generation))
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }

                if (stop.IsCancellationRequested)
                {
                    return new(SupersededConnect(), addresses);
                }

                BeginPreflightConnecting(core.Mode, request.Server.Id);
                // Только фиксированные имена категорий: произвольный flow из профиля в журнал не попадает.
                var flow = core.Server.Options.Flow switch
                {
                    null or "" => "не задан",
                    "xtls-rprx-vision" or "xtls-rprx-vision-udp443" => "Vision",
                    _ => "другой",
                };
                var mux = !core.Connection.Mux ? "выключен"
                    : string.IsNullOrEmpty(core.Server.Options.Flow) ? "запрошен" : "запрошен, отключен flow";
                var mtu = core.Connection.Mtu?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "авто";
                _journal.Add(JournalLevel.Info, "service",
                    $"Проверка профиля до VPN: протокол {core.Server.Protocol}; транспорт {core.Server.Transport.Kind}; защита {core.Server.Security.Kind}; flow {flow}; адресов {addresses.Count}; предел {core.Connection.ConnectTimeoutSeconds} с; фрагментация TLS {core.Connection.TlsFragment}; отпечаток {core.Connection.Fingerprint}/{SafeFingerprint(core)}; Mux {mux}; стек {core.Connection.Stack}; MTU {mtu}; сеть пока открыта");
            }
            finally
            {
                _switch.Release();
            }

            ConnectionPreflightResult result;
            var coreFailed = false;
            try
            {
                var outside = VpnTunnel.OutsideInterface(_options.Routes);
                _journal.Add(JournalLevel.Info, "service", $"Проверка профиля до VPN: привязка к физическому интерфейсу {(!string.IsNullOrEmpty(outside) ? "есть" : "нет")}");
                result = await ConnectionPreflight.CheckAsync(_options.XrayPath!, _options.SingBoxPath, core, addresses,
                    _options.LatencyTarget, TimeSpan.FromSeconds(core.Connection.ConnectTimeoutSeconds), _time,
                    outside, _options.Preflight, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return new(SupersededConnect(), addresses);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or TunnelException)
            {
                // В том числе ошибка резервирования порта и finally очистки ядра: RPC не остается с вечным Connecting.
                // Исключение может содержать адрес или секрет, поэтому наружу уходит только фиксированная причина.
                coreFailed = true;
                result = new(new ProbeReachability(null, ProbeFailure.Network));
            }
            finally
            {
                completed.TrySetResult();
            }

            if (!IsLatestConnect(generation))
            {
                return new(SupersededConnect(), addresses);
            }

            if (!result.Reachability.Reached)
            {
                _journal.Add(JournalLevel.Warning, "service", "Профиль не ответил до включения VPN: " + result.Reachability.Description);
                if (!coreFailed)
                {
                    _ = NoteServerTcpAsync(core.Server, addresses);
                }

                var reply = await RejectAsync(request, core.Mode, coreFailed ? ErrorCodes.CoreFailed : ErrorCodes.ServerNotResponding,
                    coreFailed ? "Не удалось запустить или завершить проверку выбранного профиля; VPN и новая блокировка не включены"
                        : "Выбранный профиль не ответил; VPN и новая блокировка не включены — выберите другой сервер",
                    generation, CancellationToken.None).ConfigureAwait(false);
                return new(reply, addresses);
            }

            var selection = result.Address is { } selected && !selected.Equals(addresses[0]) ? "резервный" : "первый";
            _journal.Add(JournalLevel.Info, "service",
                $"Выбранный профиль ответил до включения VPN; семейство адреса {result.Address?.AddressFamily}; адрес {selection}; привязка {(result.UseOutsideBinding ? "авто" : "без привязки, server-direct")}");
            return new(null, result.Address is { } answered
                ? [answered, .. addresses.Where(a => !a.Equals(answered))]
                : addresses, result.UseOutsideBinding);
        }
        finally
        {
            completed.TrySetResult();
            lock (_preflightGate)
            {
                if (ReferenceEquals(_preflightStop, stop))
                {
                    _preflightStop = null;
                    _preflightDone = null;
                }
            }
        }
    }

    private void BeginPreflightConnecting(ConnectionMode mode, string serverId)
    {
        ConnectionStatus Preparing(ConnectionStatus status) => status with
        {
            Mode = mode,
            ServerId = serverId,
            ErrorCode = null,
            CaptivePauseUntil = null,
            Since = NewAttemptSince(status.Since),
            Exit = ExitInfo.Unknown with { DirectIp = _directIp ?? status.Exit.DirectIp },
        };

        if (Status.State == ConnectionState.Connecting)
        {
            Update(s => Honest(Preparing(s) with { Protection = ProtectionChecks.For(ConnectionState.Off) }));
        }
        else
        {
            Move(Preparing, ConnectionState.Connecting, after: s => s with { Protection = ProtectionChecks.For(ConnectionState.Off) });
        }
    }

    private static string SafeFingerprint(CoreRequest core)
    {
        if (core.Server.Security.Kind == TransportSecurity.None)
        {
            return "не применяется";
        }

        var fingerprint = (core.Connection.Fingerprint == TlsFingerprint.Profile
            ? core.Server.Security.Fingerprint
            : core.Connection.Fingerprint.ToString())?.ToLowerInvariant();
        return fingerprint switch
        {
            null or "" => core.Server.Security.Kind == TransportSecurity.Reality ? "chrome" : "не задан",
            "chrome" or "firefox" or "safari" or "ios" or "android" or "edge" or "random" or "randomized" => fingerprint,
            _ => "другой",
        };
    }
}
