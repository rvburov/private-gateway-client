using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Когда окно спрашивает службу об обновлении (<see cref="UpdateWatcher"/>): по расписанию, при смене канала, по кнопке, —
/// и с какой лентой сайта: ее окно берет своим входом в аккаунт (<see cref="IUpdateFeedSource"/>).
/// </summary>
public sealed class UpdateWatcherTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeGateway _gateway = new();
    private readonly SettingsService _settings = new(new MemorySettings());
    private readonly FakeRelauncher _relauncher = new();
    private readonly FakeFeeds _feeds = new();
    private readonly UpdateWatcher _watcher;

    public UpdateWatcherTests() => _watcher = new UpdateWatcher(_gateway, _settings, _time, _relauncher, _feeds);

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
    public async Task Rollback_downloads_and_installs_the_previous_version_the_same_way()
    {
        var ct = TestContext.Current.CancellationToken;
        _gateway.Offer = new UpdateOffer(UpdateState.UpToDate, _time.GetUtcNow()) { Rollback = "0.1.0" };
        _gateway.Progress = [new UpdateProgress(150, 300), new UpdateProgress(300, 300, Ready: true)];
        await _watcher.CheckAsync(force: false, ct);

        await _watcher.DownloadAsync(ct);
        _gateway.Downloads.ShouldBeEmpty("обновления нет — качать нечего");
        _watcher.IsRollingBack.ShouldBeFalse();

        await _watcher.RollBackAsync(ct);
        (_watcher.Target, _watcher.Step, _watcher.IsRollingBack).ShouldBe(("0.1.0", UpdateStep.Ready, true));
        _gateway.Downloads.ShouldBe(["0.1.0"]);

        await _watcher.CheckAsync(force: false, ct);
        _watcher.Step.ShouldBe(UpdateStep.Ready, "служба по-прежнему предлагает откат — загрузка не пропадает");
        await _watcher.InstallAsync(ct);
        _gateway.Installs.ShouldBe(["0.1.0"]);
        _relauncher.Pids.ShouldBe([4242]);
    }

    [Fact]
    public async Task Download_the_service_no_longer_offers_is_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        _gateway.Offer = new UpdateOffer(UpdateState.UpToDate, _time.GetUtcNow()) { Rollback = "0.1.0" };
        _gateway.Progress = [new UpdateProgress(300, 300, Ready: true)];
        await _watcher.CheckAsync(force: false, ct);
        await _watcher.RollBackAsync(ct);

        _gateway.Offer = new UpdateOffer(UpdateState.UpToDate, _time.GetUtcNow());
        await _watcher.CheckAsync(force: true, ct);

        (_watcher.Step, _watcher.Target, _watcher.IsRollingBack).ShouldBe((UpdateStep.None, null, false));
        await _watcher.InstallAsync(ct);
        _gateway.Installs.ShouldBeEmpty("ставить нечего");
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

    [Fact]
    public async Task Service_gets_the_site_feed_of_the_channel_as_it_came()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.Update(s => s with { UpdateChannel = UpdateChannel.Beta });

        await _watcher.CheckAsync(force: true, ct);

        _feeds.Asked.ShouldBe([UpdateChannel.Beta], "лента — того канала, что выбран");
        _gateway.Feeds.ShouldHaveSingleItem().ShouldBeSameAs(_feeds.Feed, "окно в ленту не заглядывает — передает как есть");
        _watcher.Offer!.State.ShouldBe(UpdateState.UpToDate);
    }

    [Fact]
    public async Task Without_sign_in_neither_the_site_nor_the_service_is_asked_and_sign_in_asks_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        _feeds.SignedIn = false;
        _watcher.Start();

        await _watcher.CheckAsync(force: true, ct);

        (_feeds.Asked.Count, _gateway.Checks.Count).ShouldBe((0, 0), "входа нет — ни сайта, ни службы");
        _watcher.Offer!.State.ShouldBe(UpdateState.NoFeed, "окно просит войти");
        _watcher.Attempted.ShouldBe(_time.GetUtcNow());

        _feeds.SignedIn = true;
        _feeds.Raise();
        _gateway.Checks.ShouldBe([(UpdateChannel.Stable, false)], "вошли — проверка сразу");
        _watcher.Offer!.State.ShouldBe(UpdateState.UpToDate);

        _feeds.Raise();
        _gateway.Checks.Count.ShouldBe(1, "другие события входа — не повод, лента уже есть");
    }

    [Fact]
    public void Several_account_events_in_a_row_make_one_check_after_sign_in()
    {
        // Вход, начало и конец проверки доступа — события подряд, а до вопроса службе очередь окна доходит позже.
        var window = new QueueContext();
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(window);
        try
        {
            _feeds.SignedIn = false;
            _watcher.Start();
            _ = _watcher.CheckAsync(force: true, TestContext.Current.CancellationToken);
            _watcher.Offer!.State.ShouldBe(UpdateState.NoFeed);

            _feeds.SignedIn = true;
            _feeds.Raise();
            _feeds.Raise();
            _feeds.Raise();
            window.RunAll();

            _gateway.Checks.Count.ShouldBe(1, "одна проверка после входа, а не по проверке на событие");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    [Fact]
    public async Task Feed_the_site_did_not_give_is_a_failure_without_asking_the_service()
    {
        var ct = TestContext.Current.CancellationToken;
        _feeds.Feed = null;

        await _watcher.CheckAsync(force: true, ct);

        _gateway.Checks.ShouldBeEmpty("без ленты службе проверять нечего");
        _watcher.Offer!.State.ShouldBe(UpdateState.Failed);

        _feeds.RevokeOnAsk = true;
        await _watcher.CheckAsync(force: true, ct);
        _watcher.Offer!.State.ShouldBe(UpdateState.NoFeed, "сайт отозвал доступ (401) — входа больше нет");
    }

    [Fact]
    public async Task Demo_mode_asks_the_service_without_a_feed()
    {
        _feeds.Required = false;

        await _watcher.CheckAsync(force: false, TestContext.Current.CancellationToken);

        _feeds.Asked.ShouldBeEmpty("вход не нужен — и сайт не нужен");
        _gateway.Feeds.ShouldBe([null]);
    }

    [Fact]
    public async Task Download_takes_a_fresh_feed_because_tickets_expire()
    {
        var ct = TestContext.Current.CancellationToken;
        _gateway.Offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0") { Installable = true };
        _gateway.Progress = [new UpdateProgress(300, 300, Ready: true)];
        await _watcher.CheckAsync(force: false, ct);
        var fresh = new byte[] { 7, 7, 7 };
        _feeds.Feed = fresh;

        await _watcher.DownloadAsync(ct);

        _feeds.Asked.Count.ShouldBe(2, "перед загрузкой — лента заново");
        _gateway.DownloadFeeds.ShouldHaveSingleItem().ShouldBeSameAs(fresh);
        _watcher.Step.ShouldBe(UpdateStep.Ready);

        _feeds.Feed = null;
        await _watcher.DownloadAsync(ct);
        _watcher.Step.ShouldBe(UpdateStep.DownloadFailed, "ленты нет — качать не по чему");
        _gateway.Downloads.Count.ShouldBe(1, "служба без ссылки не спрашивается");
    }

    /// <summary>Очередь окна: работа ждет, пока тест ее не выполнит.</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Work, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunAll()
        {
            while (_queue.TryDequeue(out var item))
            {
                item.Work(item.State);
            }
        }
    }

    private sealed class FakeFeeds : IUpdateFeedSource
    {
        public bool Required { get; set; } = true;

        public bool SignedIn { get; set; } = true;

        /// <summary>Что ответит сайт; null — не ответил.</summary>
        public byte[]? Feed { get; set; } = [1, 2, 3];

        /// <summary>Сайт ответит 401: вход стирается.</summary>
        public bool RevokeOnAsk { get; set; }

        public List<UpdateChannel> Asked { get; } = [];

        public bool IsRequired => Required;

        public bool IsSignedIn => SignedIn;

        public event EventHandler? Changed;

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<byte[]?> UpdatesFeedAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
        {
            Asked.Add(channel);
            if (RevokeOnAsk)
            {
                SignedIn = false;
                Raise();
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult(Feed);
        }
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

        /// <summary>Ленты, с которыми окно спрашивало и качало.</summary>
        public List<byte[]?> Feeds { get; } = [];

        public List<byte[]?> DownloadFeeds { get; } = [];

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

        public Task<UpdateOffer?> CheckForUpdateAsync(UpdateChannel channel, bool force, byte[]? siteFeed, CancellationToken cancellationToken = default)
        {
            Checks.Add((channel, force));
            Feeds.Add(siteFeed);
            return Task.FromResult(Offer);
        }

        public IReadOnlyList<UpdateProgress> Progress { get; set; } = [];

        public List<string> Downloads { get; } = [];

        public List<string> Installs { get; } = [];

        public int? InstallerPid { get; set; } = 4242;

        public async IAsyncEnumerable<UpdateProgress> DownloadUpdateAsync(string version, byte[]? siteFeed,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Downloads.Add(version);
            DownloadFeeds.Add(siteFeed);
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
