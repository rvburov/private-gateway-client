using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Проверка узла» — замеры инспектора до узла, который назвал пользователь (этап 7, экрана в макете нет — по
/// дизайн-системе): пинг, путь (трассировка), DNS, TCP и TLS. Меряет служба (окно сеть не трогает), строки приходят по
/// мере ответа; «Остановить» обрывает замер. Под замером — как он устроен и чего не покажет. Подстраница «Проверки».
/// </summary>
public sealed partial class ProbePageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private readonly List<ProbeStep> _steps = [];
    private CancellationTokenSource? _running;
    private ProbeKind _shownKind;
    private int _shownPort;
    private bool _silent;

    public ProbePageViewModel(IGatewayClient gateway, ProtectionViewModel protection, INavigator navigator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        _gateway = gateway;
        _protection = protection;
        _navigator = navigator;
        _time = time;
        Kinds = [.. Enum.GetValues<ProbeKind>().Select(k => new ProbeKindOption(this, k))];
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var option in Kinds)
            {
                option.Refresh();
            }

            OnPropertyChanged(string.Empty);
            Show();
        };
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                OnPropertyChanged(nameof(PathNote));
                OnPropertyChanged(nameof(HasPathNote));
            }
        };
    }

    /// <summary>Выбор замера: пинг, путь, DNS, TCP, TLS.</summary>
    public IReadOnlyList<ProbeKindOption> Kinds { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPort), nameof(Method), nameof(PathNote), nameof(HasPathNote))]
    public partial ProbeKind Kind { get; set; } = ProbeKind.Ping;

    /// <summary>Узел: IP или имя.</summary>
    [ObservableProperty]
    public partial string Target { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PortText { get; set; } = "443";

    /// <summary>Порт нужен TCP и TLS.</summary>
    public bool NeedsPort => Kind is ProbeKind.Tcp or ProbeKind.Tls;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText))]
    public partial bool IsRunning { get; private set; }

    public string RunText => Loc.Instance[IsRunning ? "probe.stop" : "probe.run"];

    /// <summary>Почему замер не начат или не удался целиком; все в порядке — пусто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Итог замера одной строкой: потери и время пинга, сколько участков у пути, открыт ли порт.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string Summary { get; private set; } = string.Empty;

    public bool HasSummary => Summary.Length > 0;

    public ObservableCollection<ProbeRowViewModel> Rows { get; } = [];

    /// <summary>Сведения рукопожатия TLS: версия, шифр, сертификат.</summary>
    public ObservableCollection<DetailRowViewModel> TlsRows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasTls => TlsRows.Count > 0;

    /// <summary>Как устроен выбранный замер и чего он не покажет.</summary>
    public string Method => Loc.Instance["probe.method." + Kind.ToString().ToLowerInvariant()];

    /// <summary>Каким путем идет замер при включенной защите; выключена — пусто.</summary>
    public string PathNote => _protection.Status switch
    {
        { IsPaused: true } or { State: not ConnectionState.Connected } => string.Empty,
        { Mode: ConnectionMode.Proxy } => Loc.Instance["probe.path.proxy"],
        _ when Kind is ProbeKind.Ping or ProbeKind.Trace => Loc.Instance["probe.path.vpnIcmp"],
        _ => Loc.Instance["probe.path.vpn"],
    };

    public bool HasPathNote => PathNote.Length > 0;

    internal void Select(ProbeKind kind)
    {
        Kind = kind;
        foreach (var option in Kinds)
        {
            option.Refresh();
        }
    }

    /// <summary>«Проверить» или, пока замер идет, «Остановить».</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (_running is { } running)
        {
            await running.CancelAsync().ConfigureAwait(true);
            return;
        }

        if (ProbeTarget.Normalize(Target) is not { } target)
        {
            Error = Loc.Instance["probe.error.target"];
            return;
        }

        var port = 0;
        if (NeedsPort && (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > ProbeTarget.MaxPort))
        {
            Error = Loc.Instance["probe.error.port"];
            return;
        }

        Error = string.Empty;
        _steps.Clear();
        (_shownKind, _shownPort, _silent) = (Kind, port, false);
        Show();
        using var cancellation = new CancellationTokenSource();
        _running = cancellation;
        IsRunning = true;
        try
        {
            await foreach (var step in _gateway.ProbeAsync(_shownKind, target, port, cancellation.Token).ConfigureAwait(true))
            {
                _steps.Add(step);
                Show();
            }

            _silent = _steps.Count == 0;
        }
        catch (OperationCanceledException)
        {
            // «Остановить»: строки, что успели прийти, остаются.
        }
        finally
        {
            _running = null;
            IsRunning = false;
            Show();
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private void Show()
    {
        Rows.Clear();
        TlsRows.Clear();
        foreach (var step in _steps)
        {
            Rows.Add(new ProbeRowViewModel(_shownKind, _shownPort, step));
        }

        if (_steps.FirstOrDefault(s => s.Tls is not null)?.Tls is { } tls)
        {
            foreach (var row in TlsDetails(tls, _time.GetUtcNow()))
            {
                TlsRows.Add(row);
            }
        }

        if (_silent)
        {
            Error = Loc.Instance["probe.error.service"];
        }

        Summary = IsRunning || _steps.Count == 0 ? string.Empty : Summarize(_shownKind, _shownPort, _steps);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasTls));
    }

    /// <summary>Итог замера одной строкой.</summary>
    public static string Summarize(ProbeKind kind, int port, IReadOnlyList<ProbeStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var loc = Loc.Instance;
        if (steps is [{ Outcome: ProbeOutcome.NotFound }])
        {
            return loc["probe.summary.notFound"];
        }

        switch (kind)
        {
            case ProbeKind.Ping:
                var times = steps.Where(s => s.Outcome == ProbeOutcome.Ok && s.Time is not null).Select(s => s.Time!.Value.TotalMilliseconds).ToList();
                var loss = (int)Math.Round(100.0 * (steps.Count - times.Count) / steps.Count);
                return times.Count == 0
                    ? loc.Format("probe.summary.ping.none", steps.Count)
                    : loc.Format("probe.summary.ping", steps.Count, times.Count, loss, Ms(times.Min()), Ms(times.Average()), Ms(times.Max()));
            case ProbeKind.Trace:
                var last = steps[^1];
                return last.Outcome == ProbeOutcome.Ok ? loc.Format("probe.summary.trace", last.Index)
                    : last.Outcome == ProbeOutcome.Unreachable ? loc.Format("probe.summary.trace.unreachable", last.Index)
                    : last.Outcome == ProbeOutcome.Failed ? loc["probe.summary.failed"]
                    : loc.Format("probe.summary.trace.none", last.Index);
            case ProbeKind.Dns:
                return loc.Format("probe.summary.dns", steps.Count, Ms(steps[0].Time?.TotalMilliseconds ?? 0));
            case ProbeKind.Tcp:
            case ProbeKind.Tls:
                var step = steps[0];
                return step.Outcome switch
                {
                    ProbeOutcome.Ok when step.Tls is { Problems.Count: > 0 } => loc["probe.summary.tls.problems"],
                    ProbeOutcome.Ok when step.Tls is { } tls => loc.Format("probe.summary.tls", tls.Protocol),
                    ProbeOutcome.Ok when kind == ProbeKind.Tls => loc["probe.summary.failed"],
                    ProbeOutcome.Ok => loc.Format("probe.summary.tcp", port, Ms(step.Time?.TotalMilliseconds ?? 0)),
                    ProbeOutcome.Refused => loc.Format("probe.summary.refused", port),
                    ProbeOutcome.TimedOut => loc["probe.summary.timedOut"],
                    ProbeOutcome.Unreachable => loc["probe.summary.unreachable"],
                    _ => loc["probe.summary.failed"],
                };
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>Строки рукопожатия TLS: версия, шифр, протокол приложения, сертификат, ошибки.</summary>
    public static IEnumerable<DetailRowViewModel> TlsDetails(TlsFacts tls, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tls);
        var loc = Loc.Instance;
        yield return new DetailRowViewModel(loc["probe.tls.protocol"], tls.Protocol);
        yield return new DetailRowViewModel(loc["probe.tls.cipher"], tls.Cipher);
        if (tls.Alpn is { } alpn)
        {
            yield return new DetailRowViewModel(loc["probe.tls.alpn"], alpn);
        }

        yield return new DetailRowViewModel(loc["probe.tls.subject"], tls.Subject);
        yield return new DetailRowViewModel(loc["probe.tls.issuer"], tls.Issuer);
        var until = tls.NotAfter.ToLocalTime().ToString("d MMMM yyyy", loc.Culture);
        yield return new DetailRowViewModel(loc["probe.tls.valid"], tls.NotAfter < now
            ? loc.Format("probe.tls.expired", until)
            : loc.Format("probe.tls.until", until, Math.Max(0, (int)(tls.NotAfter - now).TotalDays)));
        if (tls.Names.Count > 0)
        {
            yield return new DetailRowViewModel(loc["probe.tls.names"], string.Join('\n', tls.Names));
        }

        yield return new DetailRowViewModel(loc["probe.tls.check"], tls.Problems.Count == 0
            ? loc["probe.tls.ok"]
            : string.Join('\n', tls.Problems.Select(p => loc["probe.tls.problem." + p.ToString().ToLowerInvariant()])));
    }

    // «24,2» или «0,4»: до сотни — с десятыми.
    private static string Ms(double ms) => ms.ToString(ms < 100 ? "0.#" : "0", Loc.Instance.Culture);
}

/// <summary>Кнопка выбора замера.</summary>
public sealed partial class ProbeKindOption(ProbePageViewModel page, ProbeKind kind) : ObservableObject
{
    public ProbeKind Kind { get; } = kind;

    public string Label => Loc.Instance["probe.kind." + Kind.ToString().ToLowerInvariant()];

    public bool IsSelected
    {
        get => page.Kind == Kind;
        set
        {
            if (value)
            {
                page.Select(Kind);
            }
        }
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(Label));
    }
}

/// <summary>Строка хода замера: номер (эхо, участок) или вид адреса, адрес, время или что случилось.</summary>
public sealed class ProbeRowViewModel
{
    public ProbeRowViewModel(ProbeKind kind, int port, ProbeStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var loc = Loc.Instance;
        Label = kind switch
        {
            ProbeKind.Dns => step.Address?.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
            ProbeKind.Tcp or ProbeKind.Tls => loc.Format("probe.port", port),
            _ => step.Index.ToString(CultureInfo.InvariantCulture),
        };
        Address = step.Address?.ToString() ?? "*";
        IsOk = step.Outcome is ProbeOutcome.Ok or ProbeOutcome.Hop;
        Result = step.Time is { } time && IsOk && kind != ProbeKind.Dns
            ? loc.Format("probe.ms", time.TotalMilliseconds.ToString(time.TotalMilliseconds < 100 ? "0.#" : "0", loc.Culture))
            : IsOk ? string.Empty : loc["probe.outcome." + step.Outcome.ToString().ToLowerInvariant()];
    }

    /// <summary>«1», «IPv4», «Порт 443».</summary>
    public string Label { get; }

    /// <summary>Кто ответил; не ответил — «*».</summary>
    public string Address { get; }

    /// <summary>«24 мс» или «нет ответа».</summary>
    public string Result { get; }

    public bool IsOk { get; }
}
