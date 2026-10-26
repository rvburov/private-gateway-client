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
    private readonly FakeRelauncher _relauncher = new();
    private readonly UpdateWatcher _watcher;

    public UpdateWatcherTests() => _watcher = new UpdateWatcher(_gateway, _settings, _time, _relauncher);

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
    public async Task New_version_found_on_schedule_is_announced_once_and_not_after_the_button_or_later()
    {
        var found = new List<string?>();
        _watcher.Found += (_, offer) => found.Add(offer.Version);
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0");
        _watcher.Start();

        _time.Advance(UpdateWatcher.FirstCheck);
        found.ShouldBe(["0.3.0"], "проверка по расписанию нашла новую версию — уведомление");
        _time.Advance(UpdateWatcher.Period);
        found.Count.ShouldBe(1, "о той же версии второй раз не говорят");

        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.1");
        await _watcher.CheckAsync(force: true, TestContext.Current.CancellationToken);
        _time.Advance(UpdateWatcher.Period);
        found.Count.ShouldBe(1, "версию нашла кнопка — ее видно на карточке, уведомление не нужно");

        _watcher.Postpone("0.3.2");
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.2");
        _time.Advance(UpdateWatcher.Period);
        found.Count.ShouldBe(1, "«Позже» для этой версии — молчать");

        _gateway.Offer = new UpdateOffer(UpdateState.UpToDate, _time.GetUtcNow());
        _time.Advance(UpdateWatcher.Period);
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.4.0");
        _settings.Update(s => s with { UpdateChannel = UpdateChannel.Beta });
        found.ShouldBe(["0.3.0", "0.4.0"], "новый канал — проверка сразу, и о новой версии сказано");
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

    [Fact]
    public async Task Update_is_downloaded_with_progress_then_installed_and_the_window_asks_to_restart()
    {
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0") { Installable = true };
        _gateway.Progress = [new UpdateProgress(0, 300), new UpdateProgress(150, 300), new UpdateProgress(300, 300, Ready: true)];
        await _watcher.CheckAsync(force: false, TestContext.Current.CancellationToken);
        var seen = new List<(UpdateStep, long)>();
        _watcher.Changed += (_, _) => seen.Add((_watcher.Step, _watcher.Received));
        var restart = 0;
        _watcher.RestartRequested += (_, _) => restart++;

        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);

        seen.ShouldBe([(UpdateStep.Downloading, 0), (UpdateStep.Downloading, 0), (UpdateStep.Downloading, 150), (UpdateStep.Ready, 300)]);
        _gateway.Downloads.ShouldBe(["0.3.0"]);

        await _watcher.InstallAsync(TestContext.Current.CancellationToken);

        _gateway.Installs.ShouldBe(["0.3.0"]);
        _relauncher.Pids.ShouldBe([4242], "окно откроется после установщика, которого запустила служба");
        (_watcher.Step, restart).ShouldBe((UpdateStep.Installing, 1));
    }

    [Fact]
    public async Task Failures_are_said_and_can_be_retried()
    {
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0") { Installable = true };
        await _watcher.CheckAsync(force: false, TestContext.Current.CancellationToken);

        _gateway.Progress = [new UpdateProgress(10, 300)];
        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);
        _watcher.Step.ShouldBe(UpdateStep.DownloadFailed, "поток кончился без итога — служба пропала");

        _gateway.Progress = [new UpdateProgress(0, 300, Error: "SHA-256 не сходится")];
        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);
        _watcher.Step.ShouldBe(UpdateStep.DownloadFailed);

        _gateway.Progress = [new UpdateProgress(300, 300, Ready: true)];
        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);
        var restarts = 0;
        _watcher.RestartRequested += (_, _) => restarts++;
        _gateway.InstallerPid = null;
        await _watcher.InstallAsync(TestContext.Current.CancellationToken);
        _watcher.Step.ShouldBe(UpdateStep.InstallFailed);
        _relauncher.Pids.ShouldBeEmpty("установщик не запущен — окно не перезапускается");
        restarts.ShouldBe(0, "и не закрывается");

        _gateway.InstallerPid = 7;
        await _watcher.InstallAsync(TestContext.Current.CancellationToken);
        _relauncher.Pids.ShouldBe([7]);
        restarts.ShouldBe(1);
    }

    [Fact]
    public async Task Only_an_installable_offer_is_downloaded_and_a_new_version_starts_over()
    {
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0");
        await _watcher.CheckAsync(force: false, TestContext.Current.CancellationToken);
        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);
        _gateway.Downloads.ShouldBeEmpty("служба сама не поставит — окно ведет на страницу выпуска");

        _gateway.Offer = _gateway.Offer with { Installable = true };
        _gateway.Progress = [new UpdateProgress(300, 300, Ready: true)];
        await _watcher.CheckAsync(force: true, TestContext.Current.CancellationToken);
        await _watcher.DownloadAsync(TestContext.Current.CancellationToken);
        _watcher.Step.ShouldBe(UpdateStep.Ready);

        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.4.0") { Installable = true };
        await _watcher.CheckAsync(force: true, TestContext.Current.CancellationToken);
        _watcher.Step.ShouldBe(UpdateStep.None, "скачанное 0.3.0 для 0.4.0 не годится");
    }

    private sealed class FakeRelauncher : IAppRelauncher
    {
        public List<int> Pids { get; } = [];

        public void RelaunchAfter(int installerPid) => Pids.Add(installerPid);
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

        public IReadOnlyList<UpdateProgress> Progress { get; set; } = [];

        public List<string> Downloads { get; } = [];

        public List<string> Installs { get; } = [];

        public int? InstallerPid { get; set; } = 4242;

        public async IAsyncEnumerable<UpdateProgress> DownloadUpdateAsync(string version, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Downloads.Add(version);
            foreach (var step in Progress)
            {
                await Task.Yield();
                yield return step;
            }
        }

        public Task<int?> InstallUpdateAsync(string version, CancellationToken cancellationToken = default)
        {
            Installs.Add(version);
            return Task.FromResult(InstallerPid);
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
