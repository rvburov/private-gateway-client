using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Метка только диагностических TCP-сокетов службы; обычные программы ее поставить не могут.</summary>
    public const uint DiagnosticMark = 0x5049;

    /// <summary>
    /// Свежая проверка под блоком без VPN: только служба через одноразовый парольный мост к точным адресам профилей.
    /// Правила пользовательского трафика, системный DNS и маршруты не меняются. Новый Connect/Disconnect отменяет
    /// измерение до взятия им _switch; исключение возвращается к прежней политике до освобождения замка.
    /// </summary>
    private async Task<IReadOnlyList<LatencyResult>?> MeasureBlockedLatencyAsync(List<ServerDefinition> servers,
        ConnectionSettings connection, Func<bool> current, Action<int, LatencyResult> progress, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _connectGeneration);
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!current() || !IsLatestConnect(generation))
            {
                return null;
            }
            if (_options.KillSwitch is not { } firewall || _held is not { } held
                || Volatile.Read(ref _tunnel) is not null && Status is not { LocalSiteFilter: true, State: ConnectionState.Connected }
                || !_blocking || Environment.ProcessPath is not { Length: > 0 } program)
            {
                return servers.Select((_, index) => Publish(index, Unavailable())).ToArray();
            }
            if (_options.XrayPath is not { } xray || !File.Exists(xray))
            {
                return servers.Select((_, index) => Publish(index, LatencyResult.Failed(LatencyFailure.CoreFailed))).ToArray();
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2), _time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var results = new LatencyResult?[servers.Count];
            var cache = new Dictionary<string, IReadOnlyList<IPAddress>?>(StringComparer.OrdinalIgnoreCase);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(connection.ConnectTimeoutSeconds, 5, 30));
            var adapter = VpnTunnel.OutsideInterface(_options.Routes);
            var localFilterCore = Status.LocalSiteFilter ? Volatile.Read(ref _core) : null;
            var watching = WatchIntentAsync();
            try
            {
                // Небольшие группы ограничивают число точных исключений и число сокетов даже у большой подписки.
                for (var offset = 0; offset < servers.Count; offset += 4)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var indexes = Enumerable.Range(offset, Math.Min(4, servers.Count - offset)).ToArray();
                    var eligible = new List<int>();
                    var allowed = new Dictionary<TransportEndpoint, IReadOnlyList<IPAddress>>();
                    foreach (var index in indexes)
                    {
                        var server = servers[index];
                        if (TransportParameterRules.RequiresUdpProbe(server))
                        {
                            results[index] = Publish(index, new LatencyResult(null, LatencyFailure.CoreFailed, "ProtectedProbeUnavailable"));
                            continue;
                        }
                        var supported = true;
                        var endpoints = new[] { new TransportEndpoint(server.Host, server.Port) }
                            .Concat(TransportParameterRules.DownloadEndpoints(server)).Distinct().ToArray();
                        foreach (var endpoint in endpoints)
                        {
                            if (!cache.TryGetValue(endpoint.Host, out var addresses))
                            {
                                addresses = await ResolveEndpointAsync(endpoint.Host).ConfigureAwait(false);
                                cache[endpoint.Host] = addresses;
                            }
                            if (addresses is null || addresses.Count == 0)
                            {
                                results[index] = Publish(index, addresses is null ? Unavailable()
                                    : new LatencyResult(null, LatencyFailure.NoResponse, "NameResolution"));
                                supported = false;
                                break;
                            }
                            allowed[endpoint] = addresses;
                        }
                        if (supported)
                        {
                            eligible.Add(index);
                        }
                    }
                    if (eligible.Count == 0)
                    {
                        continue;
                    }
                    var endpointsForRules = allowed.SelectMany(pair => pair.Value.Select(address => new KillSwitchEndpoint(ProbeAddress(address), pair.Key.Port)))
                        .Distinct().ToArray();
                    if (endpointsForRules.Length > DiagnosticProxy.MaxEndpoints || !current() || !IsLatestConnect(generation))
                    {
                        foreach (var index in eligible)
                        {
                            results[index] = Publish(index, Unavailable());
                        }
                        continue;
                    }
                    var permission = new ServiceLookup(program, DiagnosticMark, endpointsForRules);
                    if (!Try(() => { firewall.Enable(held.Policy with { Lookup = permission }); return true; }, "Исключение для диагностических сокетов не встало"))
                    {
                        foreach (var index in eligible)
                        {
                            results[index] = Publish(index, Unavailable());
                        }
                        continue;
                    }

                    await using var bridge = new DiagnosticProxy(allowed, adapter, DiagnosticMark, timeout, _options.DiagnosticConnect);
                    await LatencyProbe.MeasureProgressAsync(xray, _options.SingBoxPath, eligible.Select(index => servers[index]).ToArray(), connection,
                        _options.LatencyTarget, LatencyTimeout, _time,
                        reportResult: (position, result) => results[eligible[position]] = Publish(eligible[position], result),
                        upstream: bridge.Upstream, retryTimeout: timeout, cancellationToken: stop.Token).ConfigureAwait(false);
                    if (!Try(() => { firewall.Enable(held.Policy); return true; }, "Прежний блок после диагностической группы не вернулся"))
                    {
                        return null;
                    }
                }
                return current() && IsLatestConnect(generation) ? results.Select(result => result ?? Unavailable()).ToArray() : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Новое намерение пользователя или общий срок; готовое уже показано, оставшееся не объявляется отказом серверов.
                return null;
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await watching.ConfigureAwait(false);
                // Не сохраняем временное исключение в BootBlock. Даже отказ проверки не выключает сам блок.
                Try(() => { firewall.Enable(held.Policy); return true; }, "Прежний блок после диагностики не вернулся");
            }

            async Task WatchIntentAsync()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), _time, stop.Token).ConfigureAwait(false);
                        if (!current() || !IsLatestConnect(generation) || localFilterCore?.HasExited == true)
                        {
                            await stop.CancelAsync().ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                }
            }

            async Task<IReadOnlyList<IPAddress>?> ResolveEndpointAsync(string host)
            {
                if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
                {
                    return [ProbeAddress(literal)];
                }
                if (_options.Doh is not { } doh || !IPAddress.TryParse(doh.Host.Trim('[', ']'), out var dohAddress))
                {
                    return null;
                }
                var lookup = new ServiceLookup(program, LookupMark, [new KillSwitchEndpoint(dohAddress, doh.Port)]);
                if (!Try(() => { firewall.Enable(held.Policy with { Lookup = lookup }); return true; }, "Исключение для поиска адреса диагностического профиля не встало"))
                {
                    return null;
                }
                try
                {
                    var found = _options.LookupPastBlock is { } lookupProbe
                        ? await lookupProbe(doh, host, LookupMark, stop.Token).ConfigureAwait(false)
                        : await LatencyProbe.LookupBoundDirectAsync(doh, host, LookupMark, adapter, Timing.ProbeTimeout, stop.Token).ConfigureAwait(false);
                    return [.. PickAddresses(found.Select(ProbeAddress), _request?.Protection ?? ProtectionSettings.Default).Take(2)];
                }
                finally
                {
                    Try(() => { firewall.Enable(held.Policy); return true; }, "Блок после поиска диагностического адреса не вернулся");
                }
            }
        }
        finally
        {
            _switch.Release();
        }

        LatencyResult Publish(int index, LatencyResult result)
        {
            var stamped = Stamp(result, "direct");
            if (current() && IsLatestConnect(generation) && !cancellationToken.IsCancellationRequested)
            {
                progress(index, stamped);
            }
            return stamped;
        }

        static LatencyResult Unavailable() => new(null, LatencyFailure.CoreFailed, "ProtectionBlocked");
    }

    private static IPAddress ProbeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
