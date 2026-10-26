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

    /// <summary>«Экспорт отчета»: те же строки, что на экране, и журнал — без секретов и реального IP.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        var now = _time.GetLocalNow();
        var name = "private-gateway-report-" + now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".txt";
        if (await _files.SaveTextAsync(name, BuildReport()).ConfigureAwait(true))
        {
            _toast.Show(Loc.Instance["diag.exported"]);
        }
    }

    // ── Отчет ────────────────────────────────────────────────────────────────

    /// <summary>Текст отчета; каждая строка проходит через <see cref="SecretMasker"/>.</summary>
    public string BuildReport()
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
        lines.Add(string.Empty);
        lines.Add(loc["diag.report.journal"]);
        lines.AddRange(Journal.Reverse().Select(j => $"  {j.Time} {j.Level} {j.Source}  {j.Message}"));

        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.AppendLine(SecretMasker.Mask(line));
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
            var reason = IsRunning ? null : result?.Reason;
            return new CheckRowViewModel(
                check,
                Loc.Instance[$"diag.{Id(check)}.title"],
                result is null ? Loc.Instance["diag.checking"] : Detail(check, result.State, result.Reason),
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

    /// <summary>Пояснение: по коду уточнения, иначе по состоянию проверки, иначе общее.</summary>
    private string Detail(DiagnosticCheck check, CheckState state, string? reason)
    {
        var id = Id(check);
        var stateKey = state.ToString().ToLowerInvariant();
        var status = Protection.Status;
        switch (check)
        {
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
