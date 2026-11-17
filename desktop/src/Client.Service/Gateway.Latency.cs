using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Задержка через каждый сервер — запросом через ядро (<see cref="LatencyProbe"/>).</summary>
    public async Task<V1.MeasureLatencyReply> MeasureLatencyAsync(V1.MeasureLatencyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = new V1.MeasureLatencyReply();
        var servers = new List<ServerDefinition>();
        var entryProxies = new List<ServerDefinition?>();
        var ids = new List<string>();
        var ordinals = new List<int>();
        var ordinal = 0;
        foreach (var target in request.Targets)
        {
            ordinal++;
            try
            {
                var server = (target.Server ?? throw new ArgumentException("Нет сервера")).ToCore();
                var entryProxy = target.EntryProxy?.ToCore();
                servers.Add(server);
                entryProxies.Add(entryProxy);
                ids.Add(target.Id);
                ordinals.Add(ordinal);
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

        var session = Volatile.Read(ref _session);
        var status = Status;
        if (_blocking && status.State != ConnectionState.Connected)
        {
            reply.Results.AddRange(ids.Select(id => new LatencyResult(null, LatencyFailure.CoreFailed, "ProtectionBlocked").ToContract(id)));
            return reply;
        }

        var active = _probe;
        var connection = request.Connection?.ToCore() ?? _request?.Connection ?? ConnectionSettings.Default;
        var activeIndex = status.State == ConnectionState.Connected && !status.IsPaused && active is not null
            ? ids.IndexOf(status.ServerId ?? string.Empty) : -1;
        // IPC создает новые списки ALPN и закрепленных сертификатов. Protobuf сравнивает их содержимое,
        // record ServerDefinition — ссылки на списки: одинаковый профиль ошибочно получил бы отдельное ядро.
        if (activeIndex >= 0 && (_request is null
            || !_request.Server.ToContract(string.Empty).Equals(servers[activeIndex].ToContract(string.Empty))
            || !Equals(_request.EntryProxy?.ToContract(string.Empty), entryProxies[activeIndex]?.ToContract(string.Empty))))
        {
            activeIndex = -1;
        }

        var indexes = Enumerable.Range(0, servers.Count).Where(i => i != activeIndex).ToArray();
        var results = new LatencyResult[servers.Count];
        var attempts = new IReadOnlyList<ProbeReachability>?[servers.Count];
        if (activeIndex >= 0)
        {
            results[activeIndex] = await LatencyProbe.RequestAsync(active!.Port, _options.LatencyTarget, LatencyTimeout, _time,
                cancellationToken, new System.Net.NetworkCredential(active.User, active.Password),
                failed => attempts[activeIndex] = failed).ConfigureAwait(false);
        }

        IReadOnlyList<LatencyResult> measured = indexes.Length == 0 ? [] : _options.XrayPath is { } xray && File.Exists(xray)
            ? await LatencyProbe.MeasureAsync(xray, _options.SingBoxPath, indexes.Select(i => servers[i]).ToArray(), connection,
                _options.LatencyTarget, LatencyTimeout, _time,
                (index, failed) => attempts[indexes[index]] = failed,
                indexes.Select(i => entryProxies[i]).ToArray(), cancellationToken).ConfigureAwait(false)
            : indexes.Select(_ => LatencyResult.Failed(LatencyFailure.CoreFailed)).ToArray();
        for (var i = 0; i < indexes.Length; i++)
        {
            results[indexes[i]] = measured[i];
        }

        // Смена или обрыв VPN во время пробы не превращается в результат для новой сессии.
        if (session != Volatile.Read(ref _session))
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
}
