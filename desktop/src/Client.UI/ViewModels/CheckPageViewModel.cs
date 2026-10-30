using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
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
public sealed partial class CheckPageViewModel : ObservableObject
{
    /// <summary>Сколько последних записей журнала показывать на экране и в отчете.</summary>
    public const int JournalShown = 50;

    private const string Hidden = "•••";

    private readonly IGatewayClient _gateway;
    private readonly IServerCatalog _catalog;
    private readonly IFileSaveService _files;
    private readonly ToastViewModel _toast;
    private readonly TimeProvider _time;
    private readonly INavigator _navigator;
    private DiagnosticReport? _report;
    private ConnectionState? _reportState;
    private bool _rerun;

    public CheckPageViewModel(
        ProtectionViewModel protection,
        IGatewayClient gateway,
        IServerCatalog catalog,
        IFileSaveService files,
        ToastViewModel toast,
        TimeProvider time,
        INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(gateway);
        _navigator = navigator;
        Protection = protection;
        _gateway = gateway;
        _catalog = catalog;
        _files = files;
        _toast = toast;
        _time = time;
        gateway.JournalAdded += (_, _) => Dispatcher.UIThread.Post(RefreshJournal);
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
        Protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(ProtectionViewModel.Status) or ""))
            {
                return;
            }

            // Другое состояние — проверить заново; то же — обновить строки: IP для сайтов служба присылает в состоянии.
            if (Protection.Status.State != _reportState)
            {
                RunAgain();
            }
            else
            {
                Refresh();
            }
        };
        Refresh();
        RefreshJournal();
        // Экран открыли — проверить сразу: проверки только читают состояние.
        RunCommand.Execute(null);
    }

    public ProtectionViewModel Protection { get; }

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

    // ── Команды ──────────────────────────────────────────────────────────────

    // Кнопка не гаснет во время проверки: повторное нажатие проверит еще раз сразу после текущей.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RunAsync()
    {
        if (IsRunning)
        {
            _rerun = true;
            return;
        }

        IsRunning = true;
        Refresh();
        try
        {
            do
            {
                _rerun = false;
                // Состояние, для которого идет проверка: его смена во время проверки — повод повторить.
                _reportState = Protection.Status.State;
                _report = await _gateway.RunChecksAsync().ConfigureAwait(true);
            }
            while (_rerun);
        }
        finally
        {
            IsRunning = false;
            Refresh();
        }
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
        var loc = Loc.Instance;
        var lines = new List<string>
        {
            loc.Format("diag.report.title", AppInfo.Version),
            loc.Format("diag.report.system",
                ServiceTexts.State(_gateway.Service), AppInfo.OsName),
            loc.Format("diag.report.checkedAt", (_report?.FinishedAt ?? _time.GetUtcNow()).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
            loc.Format("diag.report.state", Protection.Label),
            string.Empty,
            loc["diag.report.checks"],
        };
        lines.AddRange(Rows.Select(r => $"  [{r.ChipText}] {r.Title} — {WithoutRealIp(r)}"));
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
        var rows = Enum.GetValues<DiagnosticCheck>().Select(check =>
        {
            var result = _report?[check];
            var state = IsRunning || result is null ? CheckState.Waiting : result.State;
            var reason = IsRunning ? null : ViaProxy(check, state) ? "proxy" : result?.Reason;
            return new CheckRowViewModel(
                check,
                Loc.Instance[$"diag.{Id(check)}.title"],
                result is null ? Loc.Instance["diag.checking"] : Detail(check, result.State, ViaProxy(check, result.State) ? "proxy" : result.Reason),
                IsRunning ? Loc.Instance["diag.state.waiting.chip"] : Chip(check, state, reason),
                state);
        }).ToList();

        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(RunText));
    }

    private void RefreshJournal()
    {
        Journal.Clear();
        foreach (var entry in _gateway.Journal.TakeLast(JournalShown).Reverse())
        {
            Journal.Add(new JournalLineViewModel(
                entry.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Level,
                entry.Source,
                SecretMasker.Mask(entry.Message)));
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
        }

        return First($"diag.{id}.{reason}", $"diag.{id}.{stateKey}", $"diag.state.{stateKey}.text");
    }

    private static string Chip(DiagnosticCheck check, CheckState state, string? reason)
    {
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
