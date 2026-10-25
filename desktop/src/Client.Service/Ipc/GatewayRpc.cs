using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service.Ipc;

/// <summary>
/// gRPC-обертка над <see cref="Gateway"/> и <see cref="ServiceJournal"/>: перевод контракта, потоки
/// WatchStatus и WatchLog. Сама ничего не решает — решает служба. Служба останавливается — потоки
/// закрываются сразу: интерфейс узнает об этом без ожидания и остановка не ждет окон.
/// </summary>
public sealed class GatewayRpc(Gateway gateway, ServiceJournal journal, V1.ServiceInfo info, IHostApplicationLifetime lifetime)
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

    /// <summary>Защиту служба применит при следующем подключении: в режиме прокси менять на ходу нечего.</summary>
    public override Task<V1.ApplyProtectionReply> ApplyProtection(V1.ProtectionSettings request, ServerCallContext context) =>
        Task.FromResult(new V1.ApplyProtectionReply());

    /// <summary>Пауза для входа в Wi-Fi или «Вернуть защиту» (<c>cancel</c>); паузы не вышло — ответ без срока.</summary>
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

    public override Task<V1.CheckReport> RunChecks(V1.RunChecksRequest request, ServerCallContext context)
    {
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

        return Task.FromResult(report);
    }

    /// <summary>Последние записи и новые по мере появления; секреты уже скрыты журналом.</summary>
    public override async Task WatchLog(V1.WatchLogRequest request, IServerStreamWriter<V1.LogEntry> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, lifetime.ApplicationStopping);
        var updates = Channel.CreateUnbounded<JournalEntry>(new UnboundedChannelOptions { SingleReader = true });
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
