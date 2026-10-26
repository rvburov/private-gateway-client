using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// События сети (лаборатория, этап 8): событие — разница двух соседних снимков состояния от службы; первый снимок — точка
/// отсчета, а не событие. Журнал помнит последние события и сообщает о новых.
/// </summary>
public sealed class NetworkEventsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly ConnectionStatus Off = ConnectionStatus.Initial(At);

    private static readonly ConnectionStatus Connected = Off with
    {
        State = ConnectionState.Connected,
        Protection = ProtectionChecks.For(ConnectionState.Connected),
    };

    [Fact]
    public void State_changes_become_events_and_the_first_snapshot_does_not()
    {
        NetworkEvents.Between(null, Connected, At).ShouldBeEmpty("состояние на момент запуска окна — не событие");
        NetworkEvents.Between(Off, Off, At).ShouldBeEmpty();

        Kinds(Off, Off with { State = ConnectionState.Connecting }).ShouldBe([NetworkEventKind.Connecting]);
        Kinds(Off with { State = ConnectionState.Connecting }, Connected).ShouldBe([NetworkEventKind.Connected]);
        Kinds(Off with { State = ConnectionState.Connecting }, Connected with { Mode = ConnectionMode.Proxy }).ShouldBe([NetworkEventKind.ProxyOn]);
        Kinds(Connected, Off).ShouldBe([NetworkEventKind.Off]);
        Kinds(Connected, Off with { StrictBlock = true, State = ConnectionState.Blocked }).ShouldBe([NetworkEventKind.StrictBlock]);

        var blocked = NetworkEvents.Between(Connected, Connected with { State = ConnectionState.Blocked, ErrorCode = ErrorCodes.ServerNotResponding }, At);
        blocked.ShouldBe([new NetworkEvent(At, NetworkEventKind.Blocked, ErrorCodes.ServerNotResponding)]);
        NetworkEvents.Between(Connected, Connected with { State = ConnectionState.Error, ErrorCode = ErrorCodes.CoreFailed }, At)
            .ShouldBe([new NetworkEvent(At, NetworkEventKind.Error, ErrorCodes.CoreFailed)]);
    }

    [Fact]
    public void Every_reconnect_attempt_is_its_own_event()
    {
        var first = Connected with { State = ConnectionState.Reconnecting, Attempt = 1 };
        var second = first with { Attempt = 2 };

        NetworkEvents.Between(Connected, first, At).ShouldBe([new NetworkEvent(At, NetworkEventKind.Reconnecting, "1")]);
        NetworkEvents.Between(first, second, At).ShouldBe([new NetworkEvent(At, NetworkEventKind.Reconnecting, "2")]);
        NetworkEvents.Between(second, second, At).ShouldBeEmpty("та же попытка — не новое событие");
    }

    [Fact]
    public void Pause_network_change_exit_and_warnings_are_events_too()
    {
        var paused = Connected with { CaptivePauseUntil = At.AddMinutes(5) };
        Kinds(Connected, paused).ShouldBe([NetworkEventKind.Paused]);
        Kinds(paused, Connected).ShouldBe([NetworkEventKind.Resumed]);
        Kinds(paused, Off).ShouldBe([NetworkEventKind.Off], "пауза кончилась выключением — это не «защита вернулась»");

        var moved = Connected with { NetworkChangedAt = At, Network = new NetworkInfo(NetworkKind.WiFi, "Cafe", true, false) };
        NetworkEvents.Between(Connected, moved, At).ShouldBe([new NetworkEvent(At, NetworkEventKind.NetworkChanged, "Cafe")]);
        NetworkEvents.Between(moved, moved, At).ShouldBeEmpty("та же смена сети — один раз");

        var measured = Connected with { Exit = new ExitInfo("203.0.113.24", null, null, null, null) };
        NetworkEvents.Between(Connected, measured, At).ShouldBe([new NetworkEvent(At, NetworkEventKind.ExitMeasured, "203.0.113.24")]);
        NetworkEvents.Between(measured, measured, At).ShouldBeEmpty();
        Kinds(Off, Off with { Exit = new ExitInfo("198.51.100.7", null, null, null, null) }).ShouldBeEmpty("без защиты адрес выхода — не событие");

        var leaking = Connected with { Protection = Connected.Protection with { Dns = CheckState.Warning, Ipv6 = CheckState.Warning } };
        NetworkEvents.Between(Connected, leaking, At).Select(e => e.Detail).ShouldBe(["dns", "ipv6"]);
        NetworkEvents.Between(leaking, leaking, At).ShouldBeEmpty("тревога, которая уже была, — не новая");
        NetworkEvents.Between(Connected, Connected with { Protection = Connected.Protection with { AllTraffic = CheckState.Warning, KillSwitch = CheckState.Warning } }, At)
            .Select(e => e.Detail).ShouldBe(["allTraffic", "killSwitch"]);
    }

    [Fact]
    public void Log_starts_from_the_current_state_and_keeps_the_latest_events()
    {
        var time = new FakeTimeProvider(At);
        var gateway = new FakeGateway { Status = Connected };
        var log = new NetworkEventLog(gateway, time);
        var added = new List<NetworkEvent>();
        log.Added += (_, e) => added.Add(e);
        gateway.Raise(Connected with { Attempt = 1, State = ConnectionState.Reconnecting });
        log.Events.ShouldBeEmpty("до Start журнал не слушает");

        log.Start();
        gateway.Raise(Off);
        time.Advance(TimeSpan.FromSeconds(3));
        gateway.Raise(Off with { State = ConnectionState.Connecting });

        log.Events.ShouldBe([new NetworkEvent(At, NetworkEventKind.Off), new NetworkEvent(At.AddSeconds(3), NetworkEventKind.Connecting)],
            "точка отсчета — состояние при Start: «переподключение» → «выключено» — событие");
        added.ShouldBe(log.Events);

        for (var i = 0; i < NetworkEventLog.Capacity; i++)
        {
            gateway.Raise(i % 2 == 0 ? Off : Off with { State = ConnectionState.Connecting });
        }

        log.Events.Count.ShouldBe(NetworkEventLog.Capacity);
        log.Events[^1].Kind.ShouldBe(NetworkEventKind.Connecting, "новые — в конце");
        log.Events.ShouldNotContain(e => e.At == At, "самое старое вытеснено");
    }

    private static List<NetworkEventKind> Kinds(ConnectionStatus before, ConnectionStatus after) =>
        [.. NetworkEvents.Between(before, after, At).Select(e => e.Kind)];

    private sealed class FakeGateway : IGatewayClient
    {
        private EventHandler<ConnectionStatus>? _statusChanged;

        public ConnectionStatus Status { get; set; } = Off;

        public ServiceKind Service => ServiceKind.Installed;

        public IReadOnlyList<JournalEntry> Journal => [];

        public event EventHandler<ConnectionStatus>? StatusChanged
        {
            add => _statusChanged += value;
            remove => _statusChanged -= value;
        }

        public event EventHandler<JournalEntry>? JournalAdded
        {
            add { }
            remove { }
        }

        public void Raise(ConnectionStatus status)
        {
            Status = status;
            _statusChanged?.Invoke(this, status);
        }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));

        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
    }
}
