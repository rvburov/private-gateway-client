using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «До и после VPN» — сетевой инспектор (этап 7, экрана в макете нет — по дизайн-системе): что видят сайты и ОС до
/// включения защиты и сейчас — адрес IPv4 и IPv6, WebRTC, выход в интернет, DNS — и вывод по каждой строке; под ними — как
/// измерено и чего замер не покажет. «До» снимает служба при включении защиты, «сейчас» — по открытию и «Сравнить».
/// </summary>
public sealed partial class ComparePageViewModel : ObservableObject
{
    private readonly IGatewayClient _gateway;
    private readonly ProtectionViewModel _protection;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;
    private NetworkComparison? _comparison;

    public ComparePageViewModel(IGatewayClient gateway, ProtectionViewModel protection, INavigator navigator, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(protection);
        _gateway = gateway;
        _protection = protection;
        _navigator = navigator;
        _time = time;
        Methods = Explain();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            Methods = Explain();
            Show();
            OnPropertyChanged(nameof(RunText));
        };
    }

    public ObservableCollection<CompareRowViewModel> Rows { get; } = [];

    /// <summary>Как измерена каждая строка и чего замер не покажет.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DetailRowViewModel> Methods { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunText))]
    public partial bool IsLoading { get; private set; }

    public string RunText => Loc.Instance[IsLoading ? "compare.running" : "compare.run"];

    [ObservableProperty]
    public partial bool IsUnavailable { get; private set; }

    /// <summary>«До — снято при включении защиты в 18:10» или почему снимка «до» нет.</summary>
    [ObservableProperty]
    public partial string BeforeText { get; private set; } = string.Empty;

    public bool HasRows => Rows.Count > 0;

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var comparison = await _gateway.CompareNetworkAsync().ConfigureAwait(true);
            IsUnavailable = comparison is null;
            _comparison = comparison ?? _comparison;
            Show();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    // Строки сравнения по порядку — и их методика.
    private static readonly string[] Keys = ["ip", "ipv6", "webrtc", "route", "dns"];

    private static DetailRowViewModel[] Explain() =>
        [.. Keys.Select(k => new DetailRowViewModel(Loc.Instance["compare." + k], Loc.Instance["compare.method." + k]))];

    private void Show()
    {
        Rows.Clear();
        if (_comparison is not { } comparison)
        {
            BeforeText = string.Empty;
            OnPropertyChanged(nameof(HasRows));
            return;
        }

        var loc = Loc.Instance;
        BeforeText = comparison.Before is { } taken
            ? loc.Format("compare.before.taken", TimeText.Day(taken.TakenAt, _time))
            : loc["compare.before.none"];
        var status = _protection.Status;
        foreach (var row in Compare(comparison, status is { State: ConnectionState.Connected, IsPaused: false }, status.Exit.DirectIp))
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>
    /// Строки сравнения с выводом: под защитой реальный адрес «сейчас» (IPv4, IPv6 или WebRTC) — утечка; сменился — защита
    /// работает; защита выключена — адрес реальный, так и должно быть. Реальный адрес — из снимка «до» и
    /// <paramref name="directIp"/> (служба меряет его и после подключения в режиме прокси).
    /// </summary>
    public static IEnumerable<CompareRowViewModel> Compare(NetworkComparison comparison, bool protectedNow, string? directIp = null)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        var loc = Loc.Instance;
        var before = comparison.Before;
        var now = comparison.Now;
        string Value(string? text) => text ?? loc["compare.none"];

        // IPv6 и STUN меряются при каждом сравнении: пусто — замер был, ответа не пришло.
        string Answer(string? text) => text ?? loc["compare.noAnswer"];

        // Реальные адреса — снятые до VPN: и IPv4, и адрес, который видел STUN.
        var real = new[] { before?.Ipv4, before?.Webrtc?.Address.ToString(), directIp }.OfType<string>().ToHashSet(StringComparer.Ordinal);

        CompareVerdict Address(string? beforeValue, string? nowValue, bool closedIsGood)
        {
            if (!protectedNow)
            {
                return CompareVerdict.Off;
            }

            if (nowValue is null)
            {
                return closedIsGood && beforeValue is not null ? CompareVerdict.Changed : CompareVerdict.Unknown;
            }

            return beforeValue is null && real.Count == 0 ? CompareVerdict.Unknown
                : nowValue == beforeValue || real.Contains(nowValue) ? CompareVerdict.Leak
                : CompareVerdict.Changed;
        }

        yield return new CompareRowViewModel("ip", Value(before?.Ipv4), Value(now.Ipv4), Address(before?.Ipv4, now.Ipv4, closedIsGood: false));
        yield return new CompareRowViewModel("ipv6", before is null ? Value(null) : Answer(before.Ipv6), Answer(now.Ipv6),
            Address(before?.Ipv6, now.Ipv6, closedIsGood: true));
        yield return new CompareRowViewModel("webrtc", Value(before?.Webrtc?.Address.ToString()), Answer(now.Webrtc?.Address.ToString()),
            Address(before?.Webrtc?.Address.ToString(), now.Webrtc?.Address.ToString(), closedIsGood: false));
        string? Route(NetworkFacts? facts) => facts?.Route is { } route
            ? facts.Gateway is { } gateway ? loc.Format("compare.route.via", route, gateway) : route
            : null;
        yield return new CompareRowViewModel("route", Value(Route(before)), Value(Route(now)),
            !protectedNow ? CompareVerdict.Off : before?.Route is null || now.Route is null ? CompareVerdict.Unknown
            : before.Route == now.Route ? CompareVerdict.Same : CompareVerdict.Changed);
        string? Dns(NetworkFacts? facts) => facts is { Dns.Count: > 0 } ? string.Join(", ", facts.Dns) : null;
        yield return new CompareRowViewModel("dns", Value(Dns(before)), Value(Dns(now)),
            !protectedNow ? CompareVerdict.Off : Dns(before) is not { } dnsBefore || Dns(now) is not { } dnsNow ? CompareVerdict.Unknown
            : dnsBefore == dnsNow ? CompareVerdict.Same : CompareVerdict.Changed);
    }
}

/// <summary>Вывод по строке сравнения.</summary>
public enum CompareVerdict
{
    /// <summary>Защита выключена: «сейчас» и есть реальная сеть.</summary>
    Off,

    /// <summary>Сменилось с включением защиты — так и должно быть.</summary>
    Changed,

    /// <summary>Осталось как было: для маршрута и DNS — бывает в режиме прокси и раздельном.</summary>
    Same,

    /// <summary>Под защитой виден реальный адрес — утечка.</summary>
    Leak,

    /// <summary>Сравнить не с чем: нет снимка «до» или замер не ответил.</summary>
    Unknown,
}

/// <summary>Строка сравнения: что, «до», «сейчас» и вывод.</summary>
public sealed class CompareRowViewModel(string key, string before, string now, CompareVerdict verdict)
{
    public string Key { get; } = key;

    public string Label { get; } = Loc.Instance["compare." + key];

    public string Before { get; } = before;

    public string Now { get; } = now;

    public CompareVerdict Verdict { get; } = verdict;

    public string VerdictText { get; } = Loc.Instance["compare.verdict." + verdict.ToString().ToLowerInvariant()];

    public bool IsLeak => Verdict == CompareVerdict.Leak;

    public bool IsGood => Verdict == CompareVerdict.Changed;
}
