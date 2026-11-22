using System.Threading.Channels;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Задержка через каждый сервер — запросом через ядро (<see cref="LatencyProbe"/>).</summary>
    public async Task<V1.MeasureLatencyReply> MeasureLatencyAsync(V1.MeasureLatencyRequest request, CancellationToken cancellationToken = default)
        => await MeasureLatencyCoreAsync(request, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Каждый готовый результат сразу; отмена закрытого потока останавливает ядро замера.</summary>
    public async IAsyncEnumerable<V1.LatencyResult> MeasureLatencyProgressAsync(V1.MeasureLatencyRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var updates = Channel.CreateBounded<(V1.LatencyResult Result, int Session, ConnectionStatus Status, ProbeLink? Link)>(new BoundedChannelOptions(Math.Max(1, request.Targets.Count))
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var measuring = ProduceAsync();
        try
        {
            await foreach (var item in updates.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (IsCurrentLatency(item.Session, item.Status, item.Link))
                {
                    yield return item.Result;
                }
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await measuring.ConfigureAwait(false);
        }

        async Task ProduceAsync()
        {
            try
            {
                await MeasureLatencyCoreAsync(request, (item, session, status, link) => updates.Writer.TryWrite((item, session, status, link)), stop.Token).ConfigureAwait(false);
                updates.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                updates.Writer.TryComplete();
            }
            catch (Exception error)
            {
                updates.Writer.TryComplete(error);
            }
        }
    }

    private async Task<V1.MeasureLatencyReply> MeasureLatencyCoreAsync(V1.MeasureLatencyRequest request,
        Action<V1.LatencyResult, int, ConnectionStatus, ProbeLink?>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = Volatile.Read(ref _session);
        var status = Status;
        var active = _probe;
        var reply = new V1.MeasureLatencyReply();
        var servers = new List<ServerDefinition>();
        var ids = new List<string>();
        var ordinals = new List<int>();
        var ordinal = 0;
        foreach (var target in request.Targets)
        {
            ordinal++;
            try
            {
                if (target.EntryProxy is not null)
                {
                    throw new ArgumentException("SOCKS5-цепочки сняты");
                }

                servers.Add((target.Server ?? throw new ArgumentException("Нет сервера")).ToCore());
                ids.Add(target.Id);
                ordinals.Add(ordinal);
            }
            catch (ArgumentException)
            {
                var invalid = Stamp(LatencyResult.Failed(LatencyFailure.Unsupported), null).ToContract(target.Id);
                reply.Results.Add(invalid);
                if (IsCurrentLatency(session, status, active))
                {
                    progress?.Invoke(invalid, session, status, active);
                }
            }
        }

        if (servers.Count == 0)
        {
            return reply;
        }

        var throughVpn = status is { Mode: ConnectionMode.Vpn, State: ConnectionState.Connected, IsPaused: false, LocalSiteFilter: false };
        var route = throughVpn ? "vpn" : "direct";
        var connection = request.Connection?.ToCore() ?? _request?.Connection ?? ConnectionSettings.Default;
        if (status.NetworkUnavailable)
        {
            foreach (var id in ids)
            {
                var unavailable = Stamp(new LatencyResult(null, LatencyFailure.CoreFailed, "NetworkUnavailable"), null).ToContract(id);
                reply.Results.Add(unavailable);
                if (IsCurrentLatency(session, status, active))
                {
                    progress?.Invoke(unavailable, session, status, active);
                }
            }
            return reply;
        }
        if (_blocking && (status.State != ConnectionState.Connected || status.LocalSiteFilter))
        {
            var measuredBlocked = await MeasureBlockedLatencyAsync(servers, connection, () => IsCurrentLatency(session, status, active),
                (index, result) => progress?.Invoke(result.ToContract(ids[index]), session, status, active), cancellationToken).ConfigureAwait(false);
            if (measuredBlocked is not null)
            {
                reply.Results.AddRange(measuredBlocked.Select((result, index) => result.ToContract(ids[index])));
            }
            return reply;
        }
        if (throughVpn && active is null)
        {
            foreach (var id in ids)
            {
                var blocked = Stamp(new LatencyResult(null, LatencyFailure.CoreFailed, "ProtectionBlocked"), null).ToContract(id);
                reply.Results.Add(blocked);
                if (IsCurrentLatency(session, status, active))
                {
                    progress?.Invoke(blocked, session, status, active);
                }
            }
            return reply;
        }

        var activeIndex = status.State == ConnectionState.Connected && !status.IsPaused && active is not null
            ? ids.IndexOf(status.ServerId ?? string.Empty) : -1;
        // IPC создает новые списки ALPN и закрепленных сертификатов. Protobuf сравнивает их содержимое,
        // record ServerDefinition — ссылки на списки: одинаковый профиль ошибочно получил бы отдельное ядро.
        if (activeIndex >= 0 && (_request is null
            || !_request.Server.ToContract(string.Empty).Equals(servers[activeIndex].ToContract(string.Empty))))
        {
            activeIndex = -1;
        }

        var indexes = Enumerable.Range(0, servers.Count).Where(i => i != activeIndex).ToArray();
        var results = new LatencyResult[servers.Count];
        var attempts = new IReadOnlyList<ProbeReachability>?[servers.Count];
        var retryTimeout = TimeSpan.FromSeconds(Math.Clamp(connection.ConnectTimeoutSeconds, 5, 30));
        // Другой профиль под VPN проверяем явно через защищенный вход действующего ядра. Не расширяем
        // правила брандмауэра и не надеемся на неявный перехват TUN: путь пробы виден в результате.
        var upstream = throughVpn && active is not null
            ? new XrayProbeUpstream(active.Port, new SocksAccount(active.User, active.Password)) : null;
        var outside = throughVpn ? null : VpnTunnel.OutsideInterface(_options.Routes);
        using var measureStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probeToken = measureStop.Token;
        await Task.WhenAll(GuardAsync(MeasureActiveAsync), GuardAsync(MeasureOthersAsync)).ConfigureAwait(false);

        async Task GuardAsync(Func<Task> measure)
        {
            try
            {
                await measure().ConfigureAwait(false);
            }
            catch
            {
                await measureStop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        async Task MeasureOthersAsync()
        {
            IReadOnlyList<LatencyResult> measured = indexes.Length == 0 ? [] : _options.XrayPath is { } xray && File.Exists(xray)
                ? await LatencyProbe.MeasureProgressAsync(xray, _options.SingBoxPath, indexes.Select(i => servers[i]).ToArray(), connection,
                    _options.LatencyTarget, LatencyTimeout, _time,
                    (index, failed) => attempts[indexes[index]] = failed,
                    (index, result) => Publish(indexes[index], result), upstream, outside, retryTimeout, probeToken).ConfigureAwait(false)
                : indexes.Select(_ => LatencyResult.Failed(LatencyFailure.CoreFailed)).ToArray();
            for (var i = 0; i < indexes.Length; i++)
            {
                if (results[indexes[i]] is null)
                {
                    Publish(indexes[i], measured[i]);
                }
            }
        }

        async Task MeasureActiveAsync()
        {
            if (activeIndex >= 0)
            {
                var result = await LatencyProbe.RequestAsync(active!.Port, _options.LatencyTarget, LatencyTimeout, _time,
                    probeToken, new System.Net.NetworkCredential(active.User, active.Password),
                    failed => attempts[activeIndex] = failed, retryTimeout).ConfigureAwait(false);
                Publish(activeIndex, result);
            }
        }

        void Publish(int index, LatencyResult result)
        {
            var stamped = Stamp(result, index == activeIndex ? "direct" : route);
            results[index] = stamped;
            if (IsCurrentLatency(session, status, active) && !cancellationToken.IsCancellationRequested)
            {
                progress?.Invoke(stamped.ToContract(ids[index]), session, status, active);
            }
        }

        // Смена или обрыв VPN во время пробы не превращается в результат для новой сессии.
        if (!IsCurrentLatency(session, status, active))
        {
            return reply;
        }

        for (var i = 0; i < attempts.Length; i++)
        {
            if (attempts[i] is { } failed)
            {
                var server = servers[i];
                var path = i == activeIndex ? "действующее ядро" : "отдельное ядро замера";
                // Только порядковый номер в этом запросе и enum: имя, id, адрес, порт, SNI и учетные данные не пишутся.
                // Снимок режима относится к началу замера и не выдается за доказательство фактического маршрута.
                _journal.Add(JournalLevel.Warning, "service",
                    $"Замер профиля #{ordinals[i]} ({server.Protocol}/{server.Transport.Kind}/{server.Security.Kind}; {path}; "
                    + $"при старте {status.Mode}/{status.State}): "
                    + string.Join("; ", failed.Select((attempt, index) => $"попытка {index + 1}: {attempt.Description}")));
            }
        }

        var failures = results.Where(r => r.Failure is not null).GroupBy(r => r.Diagnostic ?? r.Failure!.Value.ToString());
        foreach (var group in failures)
        {
            _journal.Add(JournalLevel.Warning, "service", $"Проверка задержки: {group.Count()} из {results.Length}, причина: {group.Key}");
        }

        reply.Results.AddRange(results.Select((r, i) => r.ToContract(ids[i])));
        return reply;
    }

    private LatencyResult Stamp(LatencyResult result, string? route) => result with { CheckedAt = _time.GetUtcNow(), Route = route };

    private bool IsCurrentLatency(int session, ConnectionStatus started, ProbeLink? link)
    {
        var current = Status;
        return session == Volatile.Read(ref _session) && ReferenceEquals(link, _probe)
            && current.State == started.State && current.Mode == started.Mode && current.ServerId == started.ServerId
            && current.LocalSiteFilter == started.LocalSiteFilter
            && current.NetworkChangedAt == started.NetworkChangedAt && current.NetworkUnavailable == started.NetworkUnavailable
            && current.CaptivePauseUntil == started.CaptivePauseUntil;
    }
}
