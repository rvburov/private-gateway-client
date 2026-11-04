using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Медленные и запоздалые ответы службы не удерживают строки, не заменяют новую сессию и не забивают очередь UI.</summary>
public sealed class CheckPageResponsivenessTests : WindowHarness
{
    [AvaloniaFact]
    public void Check_results_are_visible_while_delay_is_pending_and_its_late_answer_is_ignored_after_the_deadline()
    {
        var delay = new TaskCompletionSource<DelayBreakdown?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time) { Delay = _ => delay.Task };
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning && page.IsMeasuringDelay);

        page.Rows.Single(r => r.Check == DiagnosticCheck.Service).State.ShouldBe(CheckState.Ok,
            "семь сетевых замеров задержки не удерживают уже полученные результаты");
        gateway.DelayToken.IsCancellationRequested.ShouldBeFalse();

        Time.Advance(CheckPageViewModel.DelayTimeout);
        WaitFor(() => !page.IsMeasuringDelay);
        gateway.DelayToken.IsCancellationRequested.ShouldBeTrue();
        delay.SetResult(new DelayBreakdown(10, 20, 30, Time.GetUtcNow()));
        Dispatcher.UIThread.RunJobs();

        page.Delay.ShouldBeNull("поздний ответ после отмены не становится свежим замером");
        page.Dispose();
    }

    [AvaloniaFact]
    public void A_new_session_cancels_a_slow_check_and_a_late_old_report_cannot_replace_the_new_one()
    {
        var old = new TaskCompletionSource<DiagnosticReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time);
        gateway.Check = token => gateway.CheckCalls == 1 ? old.Task : Task.FromResult(Report(CheckState.Off));
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => gateway.CheckCalls == 1);
        var oldToken = gateway.CheckTokens[0];

        gateway.Push(s => s.MoveTo(ConnectionState.Off, Time.GetUtcNow()));
        WaitFor(() => gateway.CheckCalls == 2 && !page.IsRunning);
        oldToken.IsCancellationRequested.ShouldBeTrue();
        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).State.ShouldBe(CheckState.Off);

        old.SetResult(Report(CheckState.Warning));
        Dispatcher.UIThread.RunJobs();

        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).State.ShouldBe(CheckState.Off);
        gateway.CheckCalls.ShouldBe(2, "обычные обновления снимка не ставят бесконечные повторные проверки");
        page.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_new_session_in_the_same_connection_state_invalidates_the_previous_check(bool sameServer)
    {
        var old = new TaskCompletionSource<DiagnosticReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time);
        gateway.Check = token => gateway.CheckCalls == 1 ? old.Task : Task.FromResult(Report(CheckState.Ok));
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => gateway.CheckCalls == 1);

        gateway.Push(s => s with { ServerId = sameServer ? s.ServerId : "second", Since = Time.GetUtcNow().AddTicks(1) });
        WaitFor(() => gateway.CheckCalls == 2 && !page.IsRunning);
        gateway.CheckTokens[0].IsCancellationRequested.ShouldBeTrue();
        old.SetResult(Report(CheckState.Stopped));
        Dispatcher.UIThread.RunJobs();

        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).State.ShouldBe(CheckState.Ok);
        page.Dispose();
    }

    [AvaloniaFact]
    public void A_check_that_does_not_complete_stops_at_the_deadline_with_an_explicit_unknown_result()
    {
        var pending = new TaskCompletionSource<DiagnosticReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time) { Check = _ => pending.Task };
        var (window, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        page.IsRunning.ShouldBeTrue();

        Time.Advance(CheckPageViewModel.CheckTimeout);
        WaitFor(() => !page.IsRunning);

        gateway.CheckTokens[0].IsCancellationRequested.ShouldBeTrue();
        page.Rows.Count.ShouldBe(Enum.GetValues<DiagnosticCheck>().Length);
        page.Rows.ShouldAllBe(r => r.State == CheckState.Warning && r.Reason == "timeout");
        page.Rows.Where(r => r.Check != DiagnosticCheck.Dns).ShouldAllBe(r => r.ChipText == "Нет ответа"
            && r.Detail == "Проверка не завершилась вовремя — повторите ее");
        var dns = page.Rows.Single(r => r.Check == DiagnosticCheck.Dns);
        dns.ChipText.ShouldBe("Нет результата");
        dns.Detail.ShouldBe("Проверка DNS не завершилась вовремя. Это неизвестный результат, а не подтверждение отсутствия обхода. DoH/DoT браузера этой пробой не измеряются.");
        dns.IsInconclusive.ShouldBeTrue();
        dns.DisplayState.ShouldBe(CheckState.Waiting);
        dns.Icon.ShouldBe("Icon.Chip.Dash");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var rendered = window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("checkRow") && ReferenceEquals(border.DataContext, dns));
        rendered.GetVisualDescendants().OfType<Icon>().Single().Classes.ShouldNotContain("spin");
        pending.SetResult(Report(CheckState.Ok));
        Dispatcher.UIThread.RunJobs();
        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).ChipText.ShouldBe("Нет ответа");
        page.Rows.Single(r => r.Check == DiagnosticCheck.Dns).State.ShouldBe(CheckState.Warning);
        page.Rows.Single(r => r.Check == DiagnosticCheck.Dns).Reason.ShouldBe("timeout");
        page.Dispose();
    }

    [AvaloniaFact]
    public void A_stalled_hindrance_probe_does_not_replace_already_received_check_results()
    {
        var pending = new TaskCompletionSource<InterferenceProbe?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time) { Entries = _ => pending.Task };
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => gateway.EntryCalls == 1);
        page.IsRunning.ShouldBeTrue();
        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).State.ShouldBe(CheckState.Ok,
            "готовые результаты уже показаны до окончания поиска помех");

        Time.Advance(CheckPageViewModel.CheckTimeout);
        WaitFor(() => !page.IsRunning);

        page.Rows.Single(r => r.Check == DiagnosticCheck.Core).State.ShouldBe(CheckState.Ok);
        page.Hindrances.Single().ChipText.ShouldBe("Нет ответа");
        pending.SetResult(new InterferenceProbe([], []));
        Dispatcher.UIThread.RunJobs();
        page.Hindrances.Single().ChipText.ShouldBe("Нет ответа", "поздняя часть ответа не подменяет итог своего предела");
        page.Dispose();
    }

    [AvaloniaFact]
    public void A_burst_of_logs_is_coalesced_and_new_lines_do_not_rebuild_the_whole_journal()
    {
        var gateway = new ChecksGateway(Time);
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning);
        var reads = gateway.JournalReads;
        var changes = new List<NotifyCollectionChangedAction>();
        page.Journal.CollectionChanged += (_, e) => changes.Add(e.Action);

        for (var i = 0; i < 1000; i++)
        {
            gateway.AddJournal("line " + i);
        }

        gateway.JournalReads.ShouldBe(reads, "поток журнала не читает и не меняет коллекцию UI напрямую");
        Dispatcher.UIThread.RunJobs();
        gateway.JournalReads.ShouldBe(reads + 1, "на весь пакет нужен один проход очереди UI");
        page.Journal.Count.ShouldBe(CheckPageViewModel.JournalShown);
        page.Journal[0].Message.ShouldBe("line 999");
        changes.ShouldNotContain(NotifyCollectionChangedAction.Reset);
        var previousFirst = page.Journal[0];
        changes.Clear();

        gateway.AddJournal("line 1000");
        Dispatcher.UIThread.RunJobs();

        page.Journal[0].Message.ShouldBe("line 1000");
        page.Journal[1].ShouldBeSameAs(previousFirst, "неизмененные модели строк остаются на месте");
        changes.ShouldBe([NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove]);
        page.Dispose();
    }

    [AvaloniaFact]
    public void Traffic_updates_do_not_rebuild_checks_and_an_unconfirmed_ip_is_not_called_a_fresh_success()
    {
        var gateway = new ChecksGateway(Time);
        var (_, shell) = Open(gateway: _ => gateway);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.CheckedText.ShouldBe("Проверка выхода не подтверждена");
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning);
        var changes = 0;
        page.Rows.CollectionChanged += (_, _) => changes++;
        var calls = gateway.CheckCalls;

        gateway.Push(s => s with { Traffic = new TrafficStats(12, 2, 1) });
        Dispatcher.UIThread.RunJobs();

        changes.ShouldBe(0);
        gateway.CheckCalls.ShouldBe(calls);
        gateway.Push(s => s with
        {
            Protection = s.Protection with { CheckedAt = null, AllTraffic = CheckState.Warning },
            Exit = s.Exit with { Ip = "198.51.100.7", DirectIp = "198.51.100.7" },
        });
        Dispatcher.UIThread.RunJobs();
        main.CheckedText.ShouldBe("Проверка обнаружила открытый реальный IP", "неудачный повтор не скрывает доказанную утечку");
        page.Dispose();
    }

    [AvaloniaFact]
    public void Disposing_the_window_cancels_owned_page_work_and_unsubscribes_from_the_journal()
    {
        var pending = new TaskCompletionSource<DiagnosticReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new ChecksGateway(Time) { Check = _ => pending.Task };
        var (_, shell) = Open(gateway: _ => gateway);
        var page = OpenCheck(shell);
        WaitFor(() => gateway.CheckCalls == 1);

        shell.Dispose();
        var reads = gateway.JournalReads;
        gateway.CheckTokens[0].IsCancellationRequested.ShouldBeTrue();
        gateway.AddJournal("after close");
        pending.SetResult(Report(CheckState.Ok));
        Dispatcher.UIThread.RunJobs();

        gateway.JournalReads.ShouldBe(reads);
        page.Journal.ShouldBeEmpty();
    }

    private static CheckPageViewModel OpenCheck(ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        return shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
    }

    private static DiagnosticReport Report(CheckState core) => new(
        [new DiagnosticResult(DiagnosticCheck.Service, CheckState.Ok), new DiagnosticResult(DiagnosticCheck.Core, core)], DateTimeOffset.UtcNow);

    private sealed class ChecksGateway(TimeProvider time) : IGatewayClient
    {
        private readonly List<JournalEntry> _journal = [];

        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(time.GetUtcNow())
            .MoveTo(ConnectionState.Connecting, time.GetUtcNow()).MoveTo(ConnectionState.Connected, time.GetUtcNow()) with
        { ServerId = "first" };

        public ServiceKind Service => ServiceKind.Installed;

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public event EventHandler<JournalEntry>? JournalAdded;

        public Func<CancellationToken, Task<DiagnosticReport>> Check { get; set; } = _ => Task.FromResult(Report(CheckState.Ok));

        public Func<CancellationToken, Task<DelayBreakdown?>> Delay { get; init; } = _ => Task.FromResult<DelayBreakdown?>(null);

        public Func<CancellationToken, Task<InterferenceProbe?>> Entries { get; init; } = _ => Task.FromResult<InterferenceProbe?>(null);

        public int EntryCalls { get; private set; }

        public int CheckCalls { get; private set; }

        public List<CancellationToken> CheckTokens { get; } = [];

        public CancellationToken DelayToken { get; private set; }

        public int JournalReads { get; private set; }

        public IReadOnlyList<JournalEntry> Journal
        {
            get
            {
                JournalReads++;
                return [.. _journal];
            }
        }

        public void AddJournal(string message)
        {
            var entry = new JournalEntry(time.GetUtcNow(), JournalLevel.Info, "xray", message);
            _journal.Add(entry);
            JournalAdded?.Invoke(this, entry);
        }

        public void Push(Func<ConnectionStatus, ConnectionStatus> change)
        {
            Status = change(Status);
            StatusChanged?.Invoke(this, Status);
        }

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            CheckTokens.Add(cancellationToken);
            // Намеренно не учитывает отмену: запоздалый ответ старой службы не должен пройти защиту UI.
            return Check(cancellationToken);
        }

        public Task<DelayBreakdown?> MeasureDelayAsync(CancellationToken cancellationToken = default)
        {
            DelayToken = cancellationToken;
            return Delay(cancellationToken);
        }

        public Task<InterferenceProbe?> CheckEntriesAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken = default)
        {
            EntryCalls++;
            return Entries(cancellationToken);
        }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
    }
}
