using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service.Ipc;

/// <summary>
/// gRPC-обертка над <see cref="Gateway"/>, <see cref="ServiceJournal"/>, <see cref="UpdateChecker"/> и <see cref="UpdateInstaller"/>: перевод контракта,
/// потоки WatchStatus и WatchLog. Сама ничего не решает — решает служба. Служба останавливается — потоки
/// закрываются сразу: интерфейс узнает об этом без ожидания и остановка не ждет окон.
/// </summary>
public sealed class GatewayRpc(
    Gateway gateway, ServiceJournal journal, V1.ServiceInfo info, UpdateChecker updates, UpdateInstaller installer, IHostApplicationLifetime lifetime)
    : V1.GatewayService.GatewayServiceBase
{
    public override Task<V1.ServiceInfo> GetServiceInfo(V1.GetServiceInfoRequest request, ServerCallContext context) => Task.FromResult(info);

    public override Task<V1.Status> GetStatus(V1.GetStatusRequest request, ServerCallContext context) =>
        Task.FromResult(gateway.Status.ToContract());

    /// <summary>Сначала текущее состояние, потом каждое новое — пока интерфейс слушает.</summary>
    public override async Task WatchStatus(V1.WatchStatusRequest request, IServerStreamWriter<V1.Status> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var (current, updates) = gateway.Watch(stop.Token);
        try
        {
            await responseStream.WriteAsync(current.ToContract(), stop.Token).ConfigureAwait(false);
            await foreach (var status in updates.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(status.ToContract(), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Интерфейс закрылся или служба останавливается.
        }
        finally
        {
            // Подписка снимается и тогда, когда запись оборвалась ошибкой.
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    public override Task<V1.ConnectReply> Connect(V1.ConnectRequest request, ServerCallContext context) =>
        gateway.ConnectAsync(request, context?.CancellationToken ?? default);

    public override async Task<V1.DisconnectReply> Disconnect(V1.DisconnectRequest request, ServerCallContext context)
    {
        await gateway.DisconnectAsync(request?.KeepBlock == true, context?.CancellationToken ?? default).ConfigureAwait(false);
        return new V1.DisconnectReply();
    }

    public override Task<V1.MeasureLatencyReply> MeasureLatency(V1.MeasureLatencyRequest request, ServerCallContext context) =>
        gateway.MeasureLatencyAsync(request, context?.CancellationToken ?? default);

    /// <summary>Канал не задан — ошибка окна: служба не угадывает, какие выпуски ему нужны.</summary>
    public override async Task<V1.UpdateOffer> CheckForUpdate(V1.CheckForUpdateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Channel == V1.UpdateChannel.Unspecified)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Канал обновлений не задан"));
        }

        var offer = await updates.CheckAsync(request.Channel.ToCore(), request.Force, context?.CancellationToken ?? default).ConfigureAwait(false);
        return (offer with { Installable = offer.State == Core.Updates.UpdateState.Available && installer.Kind is not null }).ToContract();
    }

    /// <summary>Скачать то, что служба сама нашла последней проверкой (обновление или откат); другая версия — отказ.</summary>
    public override async Task DownloadUpdate(V1.DownloadUpdateRequest request, IServerStreamWriter<V1.UpdateDownload> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var cancellation = context?.CancellationToken ?? default;
        if (Known(request.Version) is not { } manifest)
        {
            await responseStream.WriteAsync(new V1.UpdateDownload { Error = "сначала проверьте обновления: служба не находила " + request.Version }, cancellation)
                .ConfigureAwait(false);
            return;
        }

        await foreach (var progress in installer.DownloadAsync(manifest, cancellation).ConfigureAwait(false))
        {
            await responseStream.WriteAsync(progress.ToContract(), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>Поставить скачанное обновление (или откат) той версии, что служба нашла; ответ — номер процесса установщика.</summary>
    public override Task<V1.InstallUpdateReply> InstallUpdate(V1.InstallUpdateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Known(request.Version) is not { } manifest)
        {
            return Task.FromResult(new V1.InstallUpdateReply { Error = "служба не находила " + request.Version });
        }

        try
        {
            return Task.FromResult(new V1.InstallUpdateReply { InstallerPid = installer.Install(manifest) });
        }
        catch (IOException e)
        {
            journal.Add(JournalLevel.Warning, "update", $"Обновление {manifest.Version} не поставлено: {e.Message}");
            return Task.FromResult(new V1.InstallUpdateReply { Error = e.Message });
        }
    }

    // Найденное обновление или прежняя версия для отката — только то, что служба сама нашла и проверила.
    private Core.Updates.UpdateManifest? Known(string version) =>
        updates.Found is { } found && found.Version == version ? found
        : updates.Rollback is { } rollback && rollback.Version == version ? rollback
        : null;

    /// <summary>Защиту служба применит при следующем подключении: в режиме прокси менять на ходу нечего.</summary>
    public override Task<V1.ApplyProtectionReply> ApplyProtection(V1.ProtectionSettings request, ServerCallContext context) =>
        Task.FromResult(new V1.ApplyProtectionReply());

    /// <summary>
    /// Пауза для входа в Wi-Fi (в «Защищено», «Блокировке», «Подключении», «Переподключении» и «Ошибке», где сеть закрыта) или
    /// «Вернуть защиту» / «Закрыть сеть» (<c>cancel</c>); паузы не вышло — ответ без срока.
    /// </summary>
    public override async Task<V1.CaptivePauseReply> PauseForCaptivePortal(V1.CaptivePauseRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cancel)
        {
            await gateway.ResumeAsync(context?.CancellationToken ?? default).ConfigureAwait(false);
            return new V1.CaptivePauseReply();
        }

        var until = await gateway.PauseAsync(request.Duration?.ToTimeSpan() ?? TimeSpan.Zero, context?.CancellationToken ?? default)
            .ConfigureAwait(false);
        return new V1.CaptivePauseReply { Until = until is { } at ? Timestamp.FromDateTimeOffset(at) : null };
    }

    /// <summary>
    /// Все восемь проверок «Проверки»: служба и ядро, весь трафик и блок — по состоянию, IP для сайтов, IPv6 и DNS — замером
    /// (<see cref="Gateway.CheckExitAsync"/>, <see cref="Gateway.CheckIpv6Async"/>, <see cref="Gateway.CheckDnsAsync"/>), другие VPN —
    /// по маршрутам.
    /// </summary>
    // Снимок сети читает ОС: чтение маршрутов внешней программой — не в потоке Kestrel.
    public override Task<V1.NetworkSnapshot> InspectNetwork(V1.InspectNetworkRequest request, ServerCallContext context) =>
        Task.Run(() => gateway.InspectNetwork().ToContract(), context.CancellationToken);

    public override async Task<V1.CheckReport> RunChecks(V1.RunChecksRequest request, ServerCallContext context)
    {
        // Замеры IP для сайтов по IPv4 и по IPv6 и DNS мимо туннеля — сразу: каждый ждет ответа.
        var exit = gateway.CheckExitAsync();
        var ipv6 = gateway.CheckIpv6Async();
        var dns = gateway.CheckDnsAsync();
        var (exitState, exitReason) = await exit.ConfigureAwait(false);
        var (ipv6State, ipv6Reason) = await ipv6.ConfigureAwait(false);
        var (dnsState, dnsReason) = await dns.ConfigureAwait(false);
        var status = gateway.Status;
        var report = new V1.CheckReport { FinishedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        report.Results.Add(new V1.CheckResult { Id = DiagnosticCheck.Service.ToContractId(), State = V1.CheckState.Ok });
        report.Results.Add(new V1.CheckResult
        {
            Id = DiagnosticCheck.Core.ToContractId(),
            State = status.State switch
            {
                ConnectionState.Connected => V1.CheckState.Ok,
                ConnectionState.Connecting or ConnectionState.Reconnecting => V1.CheckState.Waiting,
                ConnectionState.Error or ConnectionState.Blocked => V1.CheckState.Stopped,
                _ => V1.CheckState.Off,
            },
        });
        report.Results.Add(new V1.CheckResult
        {
            Id = DiagnosticCheck.AllTraffic.ToContractId(),
            State = status.Protection.AllTraffic.ToContract(),
            Reason = status.Mode == ConnectionMode.Proxy ? "system_proxy"
                : status.Protection.AllTraffic == CheckState.Warning && !status.IsPaused ? "split" : string.Empty,
        });
        report.Results.Add(new V1.CheckResult { Id = DiagnosticCheck.KillSwitch.ToContractId(), State = status.Protection.KillSwitch.ToContract() });
        report.Results.Add(new V1.CheckResult { Id = DiagnosticCheck.Dns.ToContractId(), State = dnsState.ToContract(), Reason = dnsReason ?? string.Empty });
        report.Results.Add(new V1.CheckResult { Id = DiagnosticCheck.Ipv6.ToContractId(), State = ipv6State.ToContract(), Reason = ipv6Reason ?? string.Empty });
        report.Results.Add(new V1.CheckResult { Id = DiagnosticCheck.ExitIp.ToContractId(), State = exitState.ToContract(), Reason = exitReason ?? string.Empty });
        if (gateway.OtherVpnAdapters() is { } other)
        {
            report.Results.Add(new V1.CheckResult
            {
                Id = DiagnosticCheck.OtherVpn.ToContractId(),
                State = other.Count == 0 ? V1.CheckState.Ok : V1.CheckState.Warning,
                Reason = other.Count == 0 ? string.Empty : "vpn_adapter",
                Details = string.Join(", ", other),
            });
        }

        return report;
    }

    /// <summary>Сколько записей журнала ждут окно, которое не успевает их читать: дальше старые выпадают, и окно узнает, сколько.</summary>
    public const int LogBuffer = 1000;

    /// <summary>
    /// Последние записи и новые по мере появления; секреты уже скрыты журналом. Очередь ограничена (<see cref="LogBuffer"/>):
    /// зависшее окно не растит память службы, а выпавшие записи — одной строкой «пропущено N».
    /// </summary>
    public override async Task WatchLog(V1.WatchLogRequest request, IServerStreamWriter<V1.LogEntry> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var dropped = 0;
        var updates = Channel.CreateBounded<JournalEntry>(
            new BoundedChannelOptions(LogBuffer) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => Interlocked.Increment(ref dropped));
        void OnAdded(object? sender, JournalEntry entry) => updates.Writer.TryWrite(entry);
        journal.Added += OnAdded;
        try
        {
            foreach (var entry in journal.Tail(request.Tail))
            {
                await responseStream.WriteAsync(Entry(entry), stop.Token).ConfigureAwait(false);
            }

            await foreach (var entry in updates.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref dropped, 0) is var lost and > 0)
                {
                    await responseStream.WriteAsync(Entry(new JournalEntry(entry.Time, JournalLevel.Warning, "service",
                        $"Пропущено записей журнала: {lost} — окно не успевало их читать")), stop.Token).ConfigureAwait(false);
                }

                await responseStream.WriteAsync(Entry(entry), stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Интерфейс закрылся или служба останавливается.
        }
        finally
        {
            journal.Added -= OnAdded;
        }
    }

    private static V1.LogEntry Entry(JournalEntry entry) => new()
    {
        Time = Timestamp.FromDateTimeOffset(entry.Time),
        Level = entry.Level.ToContract(),
        Source = entry.Source,
        Message = entry.Message,
    };
}
