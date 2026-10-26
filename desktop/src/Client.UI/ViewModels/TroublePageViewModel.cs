using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Почему сайт не открывается» — сценарий лаборатории (этап 8, экрана в макете нет — по дизайн-системе): шаги в порядке
/// запроса браузера — сеть машины, защита, DNS, TCP 443, TLS 443; замеры делает служба, следующий шаг идет, только если
/// прежние прошли (<see cref="SiteDoctor"/>). Вывод — первая найденная причина и что делать.
/// </summary>
public sealed partial class TroublePageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly SettingsService _settings;
    private readonly INavigator _navigator;
    private SiteDiagnosis? _diagnosis;

    public TroublePageViewModel(IGatewayClient gateway, ProtectionViewModel protection, SettingsService settings, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(settings);
        _gateway = gateway;
        _protection = protection;
        _settings = settings;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) => Show();
        settings.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsExpert));
            OnPropertyChanged(nameof(IsBeginner));
            Show();
        };
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                OnPropertyChanged(nameof(Via));
            }
        };
    }

    /// <summary>Шаги разбора по порядку.</summary>
    public ObservableCollection<TroubleStepViewModel> Steps { get; } = [];

    public bool HasResult => _diagnosis is not null;

    [ObservableProperty]
    public partial string Site { get; set; } = "example.com";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText))]
    public partial bool IsRunning { get; private set; }

    public string RunText => Loc.Instance[IsRunning ? "trouble.running" : "trouble.run"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; private set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>Каким путем идут замеры при нынешней защите.</summary>
    public string Via => _protection.Status switch
    {
        { IsPaused: true } or { State: not ConnectionState.Connected } => Loc.Instance["trouble.via.off"],
        { Mode: ConnectionMode.Proxy } => Loc.Instance["trouble.via.proxy"],
        _ => Loc.Instance["trouble.via.vpn"],
    };

    [ObservableProperty]
    public partial DiagnosisVerdict? Verdict { get; private set; }

    [ObservableProperty]
    public partial string VerdictTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string VerdictText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string VerdictTodo { get; private set; } = string.Empty;

    /// <summary>Тон вывода: сайт отвечает — в порядке, иначе — внимание.</summary>
    [ObservableProperty]
    public partial CheckState VerdictState { get; private set; } = CheckState.Off;

    public bool IsExpert
    {
        get => _settings.Current.LabExpert;
        set => _settings.Update(s => s with { LabExpert = value });
    }

    public bool IsBeginner
    {
        get => !IsExpert;
        set => IsExpert = !value;
    }

    /// <summary>Разбор по шагам: замер следующего шага — только если прежние прошли.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning)
        {
            return;
        }

        if (ProbeTarget.Normalize(Site) is not { } target)
        {
            Error = Loc.Instance["probe.error.target"];
            return;
        }

        Error = string.Empty;
        IsRunning = true;
        try
        {
            var facts = new SiteFacts(await _gateway.InspectNetworkAsync().ConfigureAwait(true), _protection.Status);
            if (SiteDoctor.Continue(facts))
            {
                var dns = new List<ProbeStep>();
                await foreach (var step in _gateway.ProbeAsync(ProbeKind.Dns, target, 0).ConfigureAwait(true))
                {
                    dns.Add(step);
                }

                facts = facts with { Dns = dns.Count > 0 ? dns : null };
            }

            if (facts.Dns is not null && SiteDoctor.Continue(facts))
            {
                facts = facts with { Tcp = await Last(ProbeKind.Tcp, target).ConfigureAwait(true) };
            }

            if (facts.Tcp is not null && SiteDoctor.Continue(facts))
            {
                facts = facts with { Tls = await Last(ProbeKind.Tls, target).ConfigureAwait(true) };
            }

            _diagnosis = SiteDoctor.Diagnose(facts);
            Show();
        }
        finally
        {
            IsRunning = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    private async Task<ProbeStep?> Last(ProbeKind kind, string target)
    {
        ProbeStep? last = null;
        await foreach (var step in _gateway.ProbeAsync(kind, target, 443).ConfigureAwait(true))
        {
            last = step;
        }

        return last;
    }

    private void Show()
    {
        OnPropertyChanged(nameof(Via));
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(HasResult));
        Steps.Clear();
        if (_diagnosis is not { } diagnosis)
        {
            return;
        }

        var loc = Loc.Instance;
        var key = "trouble.verdict." + char.ToLowerInvariant(diagnosis.Verdict.ToString()[0]) + diagnosis.Verdict.ToString()[1..];
        Verdict = diagnosis.Verdict;
        VerdictTitle = loc[key + ".title"];
        VerdictText = loc[key + ".text"];
        VerdictTodo = loc[key + ".todo"];
        VerdictState = diagnosis.Verdict == DiagnosisVerdict.SiteAnswers ? CheckState.Ok : CheckState.Warning;
        foreach (var step in diagnosis.Steps)
        {
            Steps.Add(new TroubleStepViewModel(step, IsExpert));
        }
    }
}

/// <summary>Шаг разбора для экрана: что проверялось, итог (как у строк «Проверки»), что нашлось и — эксперту — как мерили.</summary>
public sealed class TroubleStepViewModel
{
    public TroubleStepViewModel(DiagnosisStep step, bool expert)
    {
        ArgumentNullException.ThrowIfNull(step);
        var loc = Loc.Instance;
        var key = "trouble.step." + step.Kind.ToString().ToLowerInvariant();
        Kind = step.Kind;
        Result = step.Result;
        Title = loc[key + ".title"];
        Text = loc[key + ".text"];
        Expert = expert ? loc[key + ".expert"] : null;
        ResultText = loc["trouble.result." + step.Result.ToString().ToLowerInvariant()];
        (State, Icon) = step.Result switch
        {
            DiagnosisResult.Ok => (CheckState.Ok, "Icon.Mark.Ok"),
            DiagnosisResult.Failed => (CheckState.Warning, "Icon.Mark.Alert"),
            _ => (CheckState.Off, "Icon.Mark.Wait"),
        };
        Detail = step.Result == DiagnosisResult.Skipped ? null : Describe(step);
    }

    public DiagnosisStepKind Kind { get; }

    public DiagnosisResult Result { get; }

    public string Title { get; }

    public string Text { get; }

    public string? Expert { get; }

    public bool HasExpert => Expert is not null;

    /// <summary>Что нашлось: шлюз, адрес, время, версия TLS, что не так; не проверялось — null.</summary>
    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string ResultText { get; }

    public CheckState State { get; }

    public string Icon { get; }

    // Подробность шага словами: время — «48 мс», итог замера — «нет ответа», состояние защиты — его заголовок.
    private static string? Describe(DiagnosisStep step)
    {
        var loc = Loc.Instance;
        var parts = new List<string>();
        switch (step.Kind)
        {
            case DiagnosisStepKind.Network when step.Result == DiagnosisResult.Failed:
                parts.Add(loc["trouble.noAdapter"]);
                break;
            case DiagnosisStepKind.Protection when step.Detail is { } detail:
                parts.Add(Enum.TryParse<ConnectionState>(detail, out var state) ? loc["status." + state.ToString().ToLowerInvariant() + ".title"] : detail);
                break;
            default:
                if (step.Detail is { } found)
                {
                    parts.Add(found);
                }

                break;
        }

        if (step.Problems is { Count: > 0 } problems)
        {
            parts.AddRange(problems.Select(p => loc["probe.tls.problem." + p.ToString().ToLowerInvariant()]));
        }
        else if (step.Outcome is { } outcome and not ProbeOutcome.Ok)
        {
            parts.Add(loc["probe.outcome." + outcome.ToString().ToLowerInvariant()]);
        }

        if (step.Result == DiagnosisResult.Ok && step.Time is { } time && step.Kind != DiagnosisStepKind.Dns)
        {
            parts.Add(loc.Format("probe.ms", time.TotalMilliseconds.ToString(time.TotalMilliseconds < 100 ? "0.#" : "0", loc.Culture)));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}
