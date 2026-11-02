using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Logging;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Проверка» по макету (доска 7): восемь проверок от службы, журнал службы без секретов и
/// обезличенный отчет в файл. Проверки только читают состояние; при смене состояния защиты
/// они повторяются сами, чтобы на экране не оставался устаревший «OK».
/// </summary>
public sealed partial class CheckPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько последних записей журнала показывать на экране и в отчете.</summary>
    public const int JournalShown = 50;

    /// <summary>Общий предел запроса проверок, включая поиск мешающих настроек.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Замер задержки не удерживает результаты проверок и имеет свой предел.</summary>
    public static readonly TimeSpan DelayTimeout = TimeSpan.FromSeconds(30);

    private const string Hidden = "***";

    private readonly IGatewayClient _gateway;
    private readonly IServerCatalog _catalog;
    private readonly IFileSaveService _files;
    private readonly ToastViewModel _toast;
    private readonly TimeProvider _time;
    private readonly INavigator _navigator;
    private readonly IMachineSettings _machine;
    private readonly SettingsService _settings;
    private readonly CheckFocus _focus;
    private DiagnosticReport? _report;
    private ServiceDetails? _reportServiceDetails;
    private CheckSession _session;
    private ConnectionStatus _lastStatus;
    private CancellationTokenSource? _checksStop;
    private CancellationTokenSource? _delayStop;
    private int _journalQueued;
    private bool _disposed;
    private bool _rerun;

    public CheckPageViewModel(
        ProtectionViewModel protection,
        IGatewayClient gateway,
        IServerCatalog catalog,
        IFileSaveService files,
        ToastViewModel toast,
        TimeProvider time,
        INavigator navigator,
        IMachineSettings machine,
        SettingsService settings,
        CheckFocus focus)
    {
        ArgumentNullException.ThrowIfNull(focus);
        _focus = focus;
        focus.DelayRequested += OnDelayRequested;
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(gateway);
        _navigator = navigator;
        _machine = machine;
        _settings = settings;
        Protection = protection;
        _gateway = gateway;
        _catalog = catalog;
        _files = files;
        _toast = toast;
        _time = time;
        _lastStatus = protection.Status;
        _session = CheckSession.Of(_lastStatus);
        gateway.JournalAdded += OnJournalAdded;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        Protection.PropertyChanged += OnProtectionChanged;
        Refresh();
        RefreshJournal();
        // Экран открыли — проверить сразу: проверки только читают состояние.
        RunCommand.Execute(null);
    }

    public ProtectionViewModel Protection { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _focus.DelayRequested -= OnDelayRequested;
        _gateway.JournalAdded -= OnJournalAdded;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Protection.PropertyChanged -= OnProtectionChanged;
        _checksStop?.Cancel();
        _delayStop?.Cancel();
        GC.SuppressFinalize(this);
    }

    private void OnDelayRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed && !IsRunning && !IsMeasuringDelay)
        {
            MeasureDelayCommand.Execute(null);
        }
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => Refresh();

    private void OnProtectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName is not (nameof(ProtectionViewModel.Status) or ""))
        {
            return;
        }

        var status = Protection.Status;
        var session = CheckSession.Of(status);
        var before = _lastStatus;
        _lastStatus = status;
        if (session != _session)
        {
            _session = session;
            _checksStop?.Cancel();
            _delayStop?.Cancel();
            _report = null;
            Delay = null;
            RunAgain();
        }
        else if (before.Protection != status.Protection || before.Exit != status.Exit || before.ErrorCode != status.ErrorCode)
        {
            // Счетчики трафика приходят каждую секунду и не требуют заново строить строки проверки.
            Refresh();
        }
    }

    private void OnJournalAdded(object? sender, JournalEntry e)
    {
        if (_disposed || Interlocked.Exchange(ref _journalQueued, 1) != 0)
        {
            return;
        }

        // Сколько бы строк ядро ни прислало до следующего прохода UI, в очереди стоит только одно обновление.
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _journalQueued, 0);
            if (!_disposed)
            {
                RefreshJournal();
            }
        }, DispatcherPriority.Background);
    }

    private readonly record struct CheckSession(ConnectionState State, ConnectionMode Mode, string? Server, DateTimeOffset Since,
        bool Paused, DateTimeOffset? NetworkChangedAt)
    {
        public static CheckSession Of(ConnectionStatus status) =>
            new(status.State, status.Mode, status.ServerId, status.Since, status.IsPaused, status.NetworkChangedAt);
    }

    /// <summary>«Сеть машины» — сетевой инспектор.</summary>
    [RelayCommand]
    private void OpenNetwork() => _navigator.Navigate(Pages.Network);

    [RelayCommand]
    private void OpenProbe() => _navigator.Navigate(Pages.Probe);

    [RelayCommand]
    private void OpenConnections() => _navigator.Navigate(Pages.Connections);

    [RelayCommand]
    private void OpenCompare() => _navigator.Navigate(Pages.Compare);

    [RelayCommand]
    private void OpenPath() => _navigator.Navigate(Pages.Path);

    [RelayCommand]
    private void OpenLayers() => _navigator.Navigate(Pages.Layers);

    [RelayCommand]
    private void OpenEvents() => _navigator.Navigate(Pages.Events);

    [RelayCommand]
    private void OpenTrouble() => _navigator.Navigate(Pages.Trouble);

    [RelayCommand]
    private void OpenVpnPath() => _navigator.Navigate(Pages.VpnPath);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText))]
    public partial bool IsRunning { get; private set; }

    public string RunText => Loc.Instance[IsRunning ? "diag.running" : "diag.run"];

    public ObservableCollection<CheckRowViewModel> Rows { get; } = [];

    public ObservableCollection<JournalLineViewModel> Journal { get; } = [];

    public bool HasJournal => Journal.Count > 0;

    /// <summary>Экран открыт плиткой «Пинг» — прокрутить к блоку «Задержка»: true один раз, забирает вид.</summary>
    public bool TakeFocusDelay() => _focus.TakeDelay();

    /// <summary>«Задержка» (решение 509): последний замер службы; не мерили или не вышло — null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayRows), nameof(DelayWhy), nameof(HasDelayWhy), nameof(DelayUpdated), nameof(DelayHint), nameof(HasDelayHint))]
    public partial DelayBreakdown? Delay { get; private set; }

    /// <summary>Идет замер задержки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DelayHint), nameof(HasDelayHint), nameof(DelayUpdated))]
    [NotifyCanExecuteChangedFor(nameof(MeasureDelayCommand))]
    public partial bool IsMeasuringDelay { get; private set; }

    /// <summary>Три строки: до сервера, через сервер, новое соединение.</summary>
    public IReadOnlyList<DelayRowViewModel> DelayRows =>
    [
        new(Loc.Instance["diag.delay.toServer"], Loc.Instance["diag.delay.toServer.text"], Ms(Delay?.ToServerMs)),
        new(Loc.Instance["diag.delay.through"], Loc.Instance["diag.delay.through.text"], Ms(Delay?.ThroughServerMs)),
        new(Loc.Instance["diag.delay.fresh"], Loc.Instance["diag.delay.fresh.text"], Ms(Delay?.NewConnectionMs)),
    ];

    /// <summary>
    /// Откуда число нового соединения: сколько кругов до сервера (по замерам, а не по учебнику) и сколько длится один круг —
    /// у пользователя «480 мс» пинга против «124» у других клиентов вызывали вопрос (2026-10-31).
    /// </summary>
    public string DelayWhy => Delay is { ToServerMs: { } one, NewConnectionMs: { } fresh, Rounds: { } rounds }
        ? Loc.Instance.Format("diag.delay.why", fresh, Rounded(rounds), RoundsWord(Rounded(rounds)), Handshake, one)
        : string.Empty;

    public bool HasDelayWhy => DelayWhy.Length > 0;

    /// <summary>«Обновлено 12:34:56»; не мерили — пусто.</summary>
    public string DelayUpdated => Delay is { } delay && !IsMeasuringDelay
        ? Loc.Instance.Format("diag.delay.updated", delay.MeasuredAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
        : string.Empty;

    /// <summary>Почему чисел нет: идет замер, нет подключения, служба не ответила.</summary>
    public string DelayHint => IsMeasuringDelay ? Loc.Instance["diag.delay.measuring"]
        : Protection.Status is not { State: ConnectionState.Connected, IsPaused: false } ? Loc.Instance["diag.delay.off"]
        : Delay is null ? Loc.Instance["diag.delay.none"]
        : string.Empty;

    public bool HasDelayHint => DelayHint.Length > 0;

    /// <summary>«Обновить» задержку: служба меряет заново (несколько секунд).</summary>
    [RelayCommand(CanExecute = nameof(CanMeasureDelay))]
    private async Task MeasureDelayAsync()
    {
        if (_disposed || Protection.Status is not { State: ConnectionState.Connected, IsPaused: false })
        {
            Delay = null;
            return;
        }

        _delayStop?.Cancel();
        using var stop = new CancellationTokenSource(DelayTimeout, _time);
        _delayStop = stop;
        var session = _session;
        IsMeasuringDelay = true;
        try
        {
            var delay = await _gateway.MeasureDelayAsync(stop.Token).WaitAsync(stop.Token).ConfigureAwait(true);
            stop.Token.ThrowIfCancellationRequested();
            if (!_disposed && session == _session)
            {
                Delay = delay;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Подключение сменилось или замер не уложился в предел: прежнее число не становится новым результатом.
        }
        finally
        {
            if (ReferenceEquals(_delayStop, stop))
            {
                _delayStop = null;
                IsMeasuringDelay = false;
            }
        }
    }

    private bool CanMeasureDelay() => !IsMeasuringDelay;

    /// <summary>Рукопожатие сервера в объяснении: REALITY, TLS или протокола.</summary>
    private string Handshake => _catalog.Servers.FirstOrDefault(s => s.Id == Protection.Status.ServerId)?.Security switch
    {
        TransportSecurity.Reality => "REALITY",
        TransportSecurity.Tls => "TLS",
        _ => Loc.Instance["diag.delay.handshake.protocol"],
    };

    private static int Rounded(double rounds) => (int)Math.Max(1, Math.Round(rounds, MidpointRounding.AwayFromZero));

    /// <summary>«круг», «круга», «кругов» — по правилам языка.</summary>
    private static string RoundsWord(int count)
    {
        var form = Loc.Instance.Culture.TwoLetterISOLanguageName == "ru"
            ? count % 10 == 1 && count % 100 != 11 ? "one" : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "few" : "many"
            : count == 1 ? "one" : "many";
        return Loc.Instance["diag.delay.rounds." + form];
    }

    private static string Ms(int? value) => value is { } ms ? Loc.Instance.Format("diag.delay.ms", ms) : "—";

    /// <summary>«Что на этом компьютере может мешать» (решение 508): настройки компьютера и другие программы VPN и прокси.</summary>
    public ObservableCollection<HindranceRowViewModel> Hindrances { get; } = [];

    // ── Команды ──────────────────────────────────────────────────────────────

    // Кнопка не гаснет во время проверки: повторное нажатие проверит еще раз сразу после текущей.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (IsRunning)
        {
            _rerun = true;
            return;
        }

        IsRunning = true;
        _delayStop?.Cancel();
        try
        {
            do
            {
                _rerun = false;
                _report = null;
                Hindrances.Clear();
                Refresh();
                var session = _session;
                using var stop = new CancellationTokenSource(CheckTimeout, _time);
                _checksStop = stop;
                try
                {
                    var report = await _gateway.RunChecksAsync(stop.Token).WaitAsync(stop.Token).ConfigureAwait(true);
                    stop.Token.ThrowIfCancellationRequested();
                    if (_disposed || session != _session)
                    {
                        continue;
                    }

                    // Результаты видны сразу; измерение задержки идет отдельно и их больше не удерживает.
                    _report = report;
                    Refresh();
                    await FindHindrancesAsync(stop.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    if (!_disposed && session == _session && !_rerun)
                    {
                        if (_report is null)
                        {
                            _report = new DiagnosticReport([.. Enum.GetValues<DiagnosticCheck>()
                                .Select(check => new DiagnosticResult(check, CheckState.Warning, "timeout"))], _time.GetUtcNow());
                        }
                        else
                        {
                            // Проверки уже получены, не завершился только поиск помех: его отказ не отменяет их результат.
                            Hindrances.Clear();
                            Hindrances.Add(new HindranceRowViewModel(Loc.Instance["diag.hinder.title"], Loc.Instance["diag.timeout"],
                                Loc.Instance["diag.timeout.chip"], CheckState.Warning));
                        }
                    }
                }
                finally
                {
                    if (ReferenceEquals(_checksStop, stop))
                    {
                        _checksStop = null;
                    }
                }
            }
            while (_rerun && !_disposed);
        }
        finally
        {
            IsRunning = false;
            Refresh();
        }

        if (!_disposed && _report is not null && Protection.Status is { State: ConnectionState.Connected, IsPaused: false })
        {
            _ = MeasureDelayAsync();
        }
    }

    /// <summary>
    /// Настройки компьютера, которые могут мешать, — читает окно (<see cref="IMachineSettings"/>); слушают ли их входы прокси и
    /// какие другие программы VPN и прокси работают — отвечает служба. Без службы — настройки без проверки входов.
    /// </summary>
    private async Task FindHindrancesAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        IReadOnlyList<Hindrance> found;
        try
        {
            found = await Task.Run(() => _machine.Read(settings), cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            found = [];
        }

        var probe = await _gateway.CheckEntriesAsync([.. found.Select(h => h.Endpoint).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken)
            .WaitAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        Hindrances.Clear();
        foreach (var hindrance in found)
        {
            var entry = hindrance.Endpoint is { } endpoint
                ? probe?.Entries.FirstOrDefault(e => e.Address.Equals(endpoint, StringComparison.OrdinalIgnoreCase))
                : null;
            Hindrances.Add(HindranceRowViewModel.For(hindrance, entry, probe is not null, _machine.CanFix(hindrance, entry), FixAsync));
        }

        if (probe?.Clients is { Count: > 0 } clients)
        {
            Hindrances.Add(HindranceRowViewModel.For(clients));
        }

        if (Hindrances.Count == 0)
        {
            Hindrances.Add(HindranceRowViewModel.Nothing());
        }
    }

    /// <summary>«Исправить»: убрать настройку; прежнее значение — в файле копии, путь — в подсказке. Потом — найти заново.</summary>
    private async Task FixAsync(Hindrance hindrance)
    {
        try
        {
            var backup = await Task.Run(() => _machine.Fix(hindrance)).ConfigureAwait(true);
            _toast.Show(Loc.Instance.Format("diag.hinder.fixed", backup), ToastViewModel.LongDuration);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            _toast.Show(Loc.Instance.Format("diag.hinder.fixFailed", e.Message), ToastViewModel.LongDuration);
        }

        await FindHindrancesAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// «Экспорт отчета»: те же строки, что на экране, сеть машины, до и после VPN, сокеты счетом и журнал — обезличенно
    /// (<see cref="ReportAnonymizer"/>): публичные адреса — метками, MAC и секреты скрыты, реального IP нет.
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var now = _time.GetLocalNow();
        var name = "private-gateway-report-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt";
        using var detailsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _time);
        try
        {
            _reportServiceDetails = await _gateway.GetDetailsAsync(detailsTimeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (detailsTimeout.IsCancellationRequested)
        {
            _reportServiceDetails = null;
        }

        var snapshot = await _gateway.InspectNetworkAsync().ConfigureAwait(true);
        var comparison = await _gateway.CompareNetworkAsync().ConfigureAwait(true);
        var connections = await _gateway.ListConnectionsAsync().ConfigureAwait(true);
        var status = Protection.Status;
        var inspector = InspectorReport.Lines(snapshot, comparison, connections,
            status is { State: ConnectionState.Connected, IsPaused: false }, status.Exit.DirectIp);
        if (await _files.SaveTextAsync(name, BuildReport(inspector)).ConfigureAwait(true))
        {
            _toast.Show(Loc.Instance["diag.exported"]);
        }
    }

    // ── Отчет ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Текст отчета; каждая строка проходит через <see cref="ReportAnonymizer"/> (и <see cref="SecretMasker"/>): один
    /// адрес — одна метка во всем отчете. <paramref name="inspector"/> — раздел инспектора, если он есть.
    /// </summary>
    public string BuildReport(IEnumerable<string>? inspector = null)
    {
        // Экспорт может прийти раньше объединенного обновления очереди UI: отчет все равно берет свежий хвост.
        RefreshJournal();
        var loc = Loc.Instance;
        var lines = new List<string>
        {
            loc.Format("diag.report.title", AppInfo.Version),
            loc.Format("diag.report.windowBuild", AppInfo.Version, Known(AppInfo.Build)),
            loc.Format("diag.report.serviceBuild", Known(_reportServiceDetails?.Version), Known(_reportServiceDetails?.Build),
                Known(_reportServiceDetails?.ApiVersion)),
            loc.Format("diag.report.platform", AppInfo.OsVersion, AppInfo.Architecture),
            loc.Format("diag.report.system",
                ServiceTexts.State(_gateway.Service), AppInfo.OsName),
            loc.Format("diag.report.checkedAt", (_report?.FinishedAt ?? _time.GetUtcNow()).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            loc.Format("diag.report.state", Protection.Label),
            string.Empty,
            loc["diag.report.checks"],
        };
        lines.AddRange(Rows.Select(r => $"  [{r.ChipText}] {r.Title} — {WithoutRealIp(r)}"));
        lines.Add(string.Empty);
        lines.Add(loc["diag.report.delay"]);
        lines.AddRange(DelayRows.Select(r => $"  {r.Title}: {r.Value} — {r.Text}"));
        if (HasDelayWhy)
        {
            lines.Add("  " + DelayWhy);
        }

        if (Hindrances.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(loc["diag.report.hinder"]);
            lines.AddRange(Hindrances.Select(h => $"  [{h.ChipText}] {h.Title} — {h.Detail}"));
        }

        if (inspector is not null)
        {
            lines.Add(string.Empty);
            lines.AddRange(inspector);
        }

        // Ошибки и предупреждения всего журнала окна — отдельно: при подробном «Журнале ядра» последние строки — обычные
        // события ядер, и ошибка первой попытки подключения в них не попадала (у пользователя на Windows 2026-10-31).
        var problems = _gateway.Journal.Where(e => e.Level is JournalLevel.Warning or JournalLevel.Error).ToList();
        if (problems.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(loc["diag.report.problems"]);
            lines.AddRange(problems
                .Select(e => new JournalLineViewModel(e.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), e.Level, e.Source, SecretMasker.Mask(e.Message)))
                .Select(j => $"  {j.Time} {j.Level} {j.Source}  {j.Message}"));
        }

        lines.Add(string.Empty);
        lines.Add(loc["diag.report.journal"]);
        lines.AddRange(Journal.Reverse().Select(j => $"  {j.Time} {j.Level} {j.Source}  {j.Message}"));
        lines.Add(string.Empty);
        lines.Add(loc["diag.report.anonymous"]);

        var anonymizer = new ReportAnonymizer(loc["diag.report.addressLabel"]);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.AppendLine(anonymizer.Clean(line));
        }

        return text.ToString();

        string Known(string? value) => string.IsNullOrWhiteSpace(value) ? loc["diag.report.unknown"] : value;

        // Реальный IP пользователя в отчет не попадает, даже если он сейчас виден сайтам.
        string WithoutRealIp(CheckRowViewModel row) =>
            row.Check == DiagnosticCheck.ExitIp && Protection.Status.Exit.DirectIp is { } ip
                ? row.Detail.Replace(ip, Hidden, StringComparison.Ordinal)
                : row.Detail;
    }

    // ── Строки ───────────────────────────────────────────────────────────────

    private void RunAgain()
    {
        if (IsRunning)
        {
            _rerun = true;
        }
        else
        {
            RunCommand.Execute(null);
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var rows = Enum.GetValues<DiagnosticCheck>()
            .Where(check => check != DiagnosticCheck.VirtualMachines || OperatingSystem.IsWindows()
                || _report?[check] is { Reason: not "not_applicable" and not "simulated" })
            .Select(check =>
        {
            var result = _report?[check] ?? (check == DiagnosticCheck.VirtualMachines && _report is not null
                ? new DiagnosticResult(check, CheckState.Waiting, "not_checked") : null);
            var state = result is null ? CheckState.Waiting : result.State;
            var reason = ViaProxy(check, state) ? "proxy" : result?.Reason;
            return new CheckRowViewModel(
                check,
                Loc.Instance[$"diag.{Id(check)}.title"],
                result is null ? Loc.Instance["diag.checking"] : Detail(check, result.State, ViaProxy(check, result.State) ? "proxy" : result.Reason),
                Chip(check, state, reason),
                state);
        }).ToList();

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(DelayHint));
        OnPropertyChanged(nameof(HasDelayHint));
    }

    private void RefreshJournal()
    {
        if (_disposed)
        {
            return;
        }

        var lines = _gateway.Journal.TakeLast(JournalShown).Reverse().Select(entry => new JournalLineViewModel(
            entry.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            entry.Level,
            entry.Source,
            SecretMasker.Mask(entry.Message))).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < Journal.Count && Journal[i] == lines[i])
            {
                continue;
            }

            var existing = -1;
            for (var j = i + 1; j < Journal.Count; j++)
            {
                if (Journal[j] == lines[i])
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
            {
                Journal.Move(existing, i);
            }
            else
            {
                Journal.Insert(i, lines[i]);
            }
        }

        while (Journal.Count > lines.Count)
        {
            Journal.RemoveAt(Journal.Count - 1);
        }

        OnPropertyChanged(nameof(HasJournal));
    }

    /// <summary>
    /// IP сервера в режиме прокси видят только сайты, которые открывают программы через прокси ядра: остальные идут напрямую.
    /// «Скрыт» здесь вводил в заблуждение (у пользователя на Windows 2026-10-30 браузер шел мимо прокси, а проверка писала
    /// «Скрыт»).
    /// </summary>
    private bool ViaProxy(DiagnosticCheck check, CheckState state) =>
        check == DiagnosticCheck.ExitIp && state == CheckState.Ok && Protection.Status.Mode == ConnectionMode.Proxy;

    /// <summary>Пояснение: по коду уточнения, иначе по состоянию проверки, иначе общее.</summary>
    private string Detail(DiagnosticCheck check, CheckState state, string? reason)
    {
        if (reason == "timeout")
        {
            return Loc.Instance["diag.timeout"];
        }

        var id = Id(check);
        var stateKey = state.ToString().ToLowerInvariant();
        var status = Protection.Status;
        switch (check)
        {
            case DiagnosticCheck.ExitIp when reason == "proxy":
                return Loc.Instance.Format("diag.exit_ip.proxy", status.Exit.Ip ?? Hidden);
            case DiagnosticCheck.ExitIp when state == CheckState.Ok && status.Exit.Ip is not null:
                return string.Join(" · ", new[] { status.Exit.Ip, status.Exit.City, status.Exit.NetworkType }
                    .Where(s => !string.IsNullOrEmpty(s)));
            case DiagnosticCheck.ExitIp when state == CheckState.Off:
                return Loc.Instance.Format("diag.exit_ip.off", status.Exit.DirectIp ?? Hidden);
            case DiagnosticCheck.Core when state == CheckState.Ok:
                var server = _catalog.Servers.FirstOrDefault(s => s.Id == status.ServerId);
                return Loc.Instance.Format("diag.core.ok", ServerFacts.CoreName(server?.Protocol ?? ProxyProtocol.Vless));
            case DiagnosticCheck.KillSwitch when state == CheckState.Ok:
                return Loc.Instance.Format("diag.kill_switch.ok", FirewallName);
            case DiagnosticCheck.VirtualMachines:
                return First($"diag.{id}.{reason}", $"diag.{id}.{stateKey}", "diag.virtual_machines.not_checked")
                    + " " + Loc.Instance["diag.virtual_machines.scope"];
        }

        return First($"diag.{id}.{reason}", $"diag.{id}.{stateKey}", $"diag.state.{stateKey}.text");
    }

    private static string Chip(DiagnosticCheck check, CheckState state, string? reason)
    {
        if (reason == "timeout")
        {
            return Loc.Instance["diag.timeout.chip"];
        }

        var id = Id(check);
        var stateKey = state.ToString().ToLowerInvariant();
        return First($"diag.{id}.{reason}.chip", $"diag.{id}.{stateKey}.chip", $"diag.state.{stateKey}.chip");
    }

    private static string First(params string[] keys) =>
        Loc.Instance[keys.FirstOrDefault(Loc.Instance.Has) ?? keys[^1]];

    /// <summary>Ключ проверки в словаре — как ее идентификатор в контракте IPC.</summary>
    private static string Id(DiagnosticCheck check) => check switch
    {
        DiagnosticCheck.AllTraffic => "all_traffic",
        DiagnosticCheck.KillSwitch => "kill_switch",
        DiagnosticCheck.ExitIp => "exit_ip",
        DiagnosticCheck.OtherVpn => "other_vpn",
        DiagnosticCheck.VirtualMachines => "virtual_machines",
        _ => check.ToString().ToLowerInvariant(),
    };

    /// <summary>Чем ОС держит блок при обрыве — так и пишется в проверке.</summary>
    private static string FirewallName =>
        OperatingSystem.IsWindows() ? "Windows Filtering Platform" : OperatingSystem.IsMacOS() ? "pf" : "nftables";
}

/// <summary>Строка проверки: кружок и чип по состоянию, заголовок, пояснение.</summary>
public sealed record CheckRowViewModel(DiagnosticCheck Check, string Title, string Detail, string ChipText, CheckState State)
{
    public string Icon => CheckIcon.For(State);
}

/// <summary>Строка журнала: время, уровень (INF, WRN…), источник и текст без секретов.</summary>
public sealed record JournalLineViewModel(string Time, JournalLevel LevelValue, string Source, string Message)
{
    public string Level => LevelValue switch
    {
        JournalLevel.Debug => "DBG",
        JournalLevel.Warning => "WRN",
        JournalLevel.Error => "ERR",
        _ => "INF",
    };

    public bool IsWarning => LevelValue == JournalLevel.Warning;

    public bool IsError => LevelValue == JournalLevel.Error;

    public bool IsDebug => LevelValue == JournalLevel.Debug;
}

/// <summary>Строка блока «Задержка»: что измерено, пояснение и число.</summary>
public sealed record DelayRowViewModel(string Title, string Text, string Value);

