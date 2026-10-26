using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Когда окно спрашивает службу об обновлении (<see cref="UpdateWatcher"/>): по расписанию, при смене канала, по кнопке.</summary>
public sealed class UpdateWatcherTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGateway _gateway = new();
    private readonly SettingsService _settings = new(new MemorySettings());
    private readonly UpdateWatcher _watcher;

    public UpdateWatcherTests() => _watcher = new UpdateWatcher(_gateway, _settings, _time);

    public void Dispose() => _watcher.Dispose();

    [Fact]
    public void Checks_two_minutes_after_start_and_then_every_twelve_hours()
    {
        _watcher.Start();

        _time.Advance(UpdateWatcher.FirstCheck - TimeSpan.FromSeconds(1));
        _gateway.Checks.ShouldBeEmpty("старт окна важнее");
        _time.Advance(TimeSpan.FromSeconds(1));
        _gateway.Checks.ShouldBe([(UpdateChannel.Stable, false)]);
        _watcher.Offer!.State.ShouldBe(UpdateState.UpToDate);

        _time.Advance(UpdateWatcher.Period - TimeSpan.FromMinutes(10));
        _gateway.Checks.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(10));
        _gateway.Checks.Count.ShouldBe(2, "раз в 12 часов");
    }

    [Fact]
    public async Task With_automatic_checks_off_only_the_button_asks()
    {
        _settings.Update(s => s with { CheckUpdates = false });
        _watcher.Start();

        _time.Advance(TimeSpan.FromDays(2));
        _gateway.Checks.ShouldBeEmpty("выключено — окно само за обновлениями не ходит");

        _settings.Update(s => s with { UpdateChannel = UpdateChannel.Beta });
        _gateway.Checks.ShouldBeEmpty("и смена канала не повод, пока ни разу не спрашивали");

        await _watcher.CheckAsync(force: true, TestContext.Current.CancellationToken);
        _gateway.Checks.ShouldBe([(UpdateChannel.Beta, true)]);

        _settings.Update(s => s with { UpdateChannel = UpdateChannel.Stable });
        _gateway.Checks.Last().ShouldBe((UpdateChannel.Stable, false), "кнопкой уже спрашивали — на экране ответ для старого канала, нужен новый");
    }

    [Fact]
    public void New_channel_is_asked_at_once()
    {
        _watcher.Start();

        _settings.Update(s => s with { UpdateChannel = UpdateChannel.Beta });

        _gateway.Checks.ShouldBe([(UpdateChannel.Beta, false)]);
        _settings.Update(s => s with { Language = "en" });
        _gateway.Checks.Count.ShouldBe(1, "другие настройки — не повод");
    }

    [Fact]
    public async Task Silent_service_leaves_no_offer_and_later_is_remembered_per_version()
    {
        _gateway.Offer = null;
        await _watcher.CheckAsync(force: false, TestContext.Current.CancellationToken);
        _watcher.Offer.ShouldBeNull();
        _watcher.Attempted.ShouldBe(_time.GetUtcNow());

        var changes = 0;
        _watcher.Changed += (_, _) => changes++;
        _watcher.Postpone("0.3.0");
        _watcher.Postponed.ShouldBe("0.3.0");
        changes.ShouldBe(1);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _value = AppSettings.Default;

        public AppSettings Load() => _value;

        public void Save(AppSettings settings) => _value = settings;
    }

    private sealed class FakeGateway : IGatewayClient
    {
        public UpdateOffer? Offer { get; set; } = new(UpdateState.UpToDate, DateTimeOffset.UnixEpoch);

        public List<(UpdateChannel Channel, bool Force)> Checks { get; } = [];

        public ConnectionStatus Status => ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);

        public ServiceKind Service => ServiceKind.Installed;

        public IReadOnlyList<JournalEntry> Journal => [];

        public event EventHandler<ConnectionStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<JournalEntry>? JournalAdded
        {
            add { }
            remove { }
        }

        public Task<UpdateOffer?> CheckForUpdateAsync(UpdateChannel channel, bool force, CancellationToken cancellationToken = default)
        {
            Checks.Add((channel, force));
            return Task.FromResult(Offer);
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
