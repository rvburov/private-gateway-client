using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Подключение и ядро» по макету (доска 6): ядра, TUN, соединение, режим прокси. Сохраняется
/// сразу, службе уходит при подключении; открыть прокси для локальной сети — с вопросом.
/// </summary>
public sealed partial class CorePageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OverlayViewModel _overlay;
    private readonly INavigator _navigator;

    public CorePageViewModel(SettingsService settings, OverlayViewModel overlay, INavigator navigator)
    {
        _settings = settings;
        _overlay = overlay;
        _navigator = navigator;
        LogOptions = Options(Enum.GetValues<CoreLogLevel>(), LogLabel, () => C.CoreLog, v => Set(c => c with { CoreLog = v }), () => IsLogOpen = false);
        StackOptions = Options(Enum.GetValues<TunEngine>(), StackLabel, () => C.Stack, v => Set(c => c with { Stack = v }), () => IsStackOpen = false);
        MtuOptions =
        [
            .. ConnectionSettings.MtuChoices.Select(v => new ChoiceOptionViewModel(
                () => MtuLabel(v), () => C.Mtu == v, () => ChooseMtu(v))),
            new ChoiceOptionViewModel(() => Loc.Instance["core.mtu.custom"], () => C.Mtu is { } m && !ConnectionSettings.MtuChoices.Contains(m), OpenCustomMtu),
        ];
        FingerprintOptions = Options(Enum.GetValues<TlsFingerprint>(), FingerprintLabel, () => C.Fingerprint, v => Set(c => c with { Fingerprint = v }), () => IsFingerprintOpen = false);
        TimeoutOptions = [.. ConnectionSettings.TimeoutChoices.Select(v => new ChoiceOptionViewModel(
            () => TimeoutLabel(v), () => C.ConnectTimeoutSeconds == v, () => { IsTimeoutOpen = false; Set(c => c with { ConnectTimeoutSeconds = v }); }))];
        ReconnectOptions = Options(Enum.GetValues<ReconnectPolicy>(), ReconnectLabel, () => C.Reconnect, v => Set(c => c with { Reconnect = v }), () => IsReconnectOpen = false);
        settings.Changed += (_, _) => Refresh();
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    private ConnectionSettings C => _settings.Current.Connection;

    // ── Ядро ─────────────────────────────────────────────────────────────────

    /// <summary>Версия Xray-core от службы; пока ее нет — «не установлено».</summary>
    public string XrayVersion => Loc.Instance["core.version.none"];

    /// <summary>Второе ядро (Hysteria2, TUIC) выбирается после технического спайка.</summary>
    public string SecondCore => Loc.Instance["core.second.none"];

    public IReadOnlyList<ChoiceOptionViewModel> LogOptions { get; }

    [ObservableProperty]
    public partial bool IsLogOpen { get; set; }

    public string LogText => LogLabel(C.CoreLog);

    // ── TUN ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<ChoiceOptionViewModel> StackOptions { get; }

    [ObservableProperty]
    public partial bool IsStackOpen { get; set; }

    public string StackText => StackLabel(C.Stack);

    public IReadOnlyList<ChoiceOptionViewModel> MtuOptions { get; }

    [ObservableProperty]
    public partial bool IsMtuOpen { get; set; }

    public string MtuText => MtuLabel(C.Mtu);

    public bool StrictRoutes
    {
        get => C.StrictRoutes;
        set => Set(c => c with { StrictRoutes = value });
    }

    public bool Sniffing
    {
        get => C.Sniffing;
        set => Set(c => c with { Sniffing = value });
    }

    // ── Соединение ───────────────────────────────────────────────────────────

    public IReadOnlyList<ChoiceOptionViewModel> FingerprintOptions { get; }

    [ObservableProperty]
    public partial bool IsFingerprintOpen { get; set; }

    public string FingerprintText => FingerprintLabel(C.Fingerprint);

    public bool Mux
    {
        get => C.Mux;
        set => Set(c => c with { Mux = value });
    }

    public bool TlsFragment
    {
        get => C.TlsFragment;
        set => Set(c => c with { TlsFragment = value });
    }

    public IReadOnlyList<ChoiceOptionViewModel> TimeoutOptions { get; }

    [ObservableProperty]
    public partial bool IsTimeoutOpen { get; set; }

    public string TimeoutText => TimeoutLabel(C.ConnectTimeoutSeconds);

    public IReadOnlyList<ChoiceOptionViewModel> ReconnectOptions { get; }

    [ObservableProperty]
    public partial bool IsReconnectOpen { get; set; }

    public string ReconnectText => ReconnectLabel(C.Reconnect);

    // ── Режим прокси ─────────────────────────────────────────────────────────

    public string ProxyPort => C.ProxyPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>Открыть — только после «Открыть» в диалоге; закрыть — сразу.</summary>
    public bool ProxyAllowLan
    {
        get => C.ProxyAllowLan;
        set
        {
            if (!value || C.ProxyAllowLan)
            {
                Set(c => c with { ProxyAllowLan = value });
                return;
            }

            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ProxyAllowLan)));
            _ = OpenProxyToLanAsync();
        }
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    [RelayCommand]
    private void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    private void ToggleStack() => IsStackOpen = !IsStackOpen;

    [RelayCommand]
    private void ToggleMtu() => IsMtuOpen = !IsMtuOpen;

    [RelayCommand]
    private void ToggleFingerprint() => IsFingerprintOpen = !IsFingerprintOpen;

    [RelayCommand]
    private void ToggleTimeout() => IsTimeoutOpen = !IsTimeoutOpen;

    [RelayCommand]
    private void ToggleReconnect() => IsReconnectOpen = !IsReconnectOpen;

    internal void SaveMtu(int mtu) => Set(c => c with { Mtu = mtu });

    private void ChooseMtu(int? value)
    {
        IsMtuOpen = false;
        Set(c => c with { Mtu = value });
    }

    private void OpenCustomMtu()
    {
        IsMtuOpen = false;
        _overlay.ShowSheet(new MtuViewModel(C.Mtu, this, _overlay));
    }

    private async Task OpenProxyToLanAsync()
    {
        if (await SafetyDialogs.ConfirmLanProxyAsync(_overlay).ConfigureAwait(true))
        {
            Set(c => c with { ProxyAllowLan = true });
        }
    }

    private void Set(Func<ConnectionSettings, ConnectionSettings> change) =>
        _settings.Update(s => s with { Connection = change(s.Connection) });

    private void Refresh()
    {
        foreach (var option in LogOptions.Concat(StackOptions).Concat(MtuOptions).Concat(FingerprintOptions).Concat(TimeoutOptions).Concat(ReconnectOptions))
        {
            option.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }

    private static ChoiceOptionViewModel[] Options<T>(T[] values, Func<T, string> label, Func<T> current, Action<T> set, Action close)
        where T : struct, Enum =>
        [.. values.Select(v => new ChoiceOptionViewModel(() => label(v), () => EqualityComparer<T>.Default.Equals(current(), v), () =>
        {
            close();
            set(v);
        }))];

    private static string LogLabel(CoreLogLevel value) => Loc.Instance[value switch
    {
        CoreLogLevel.Info => "core.log.info",
        CoreLogLevel.Debug => "core.log.debug",
        _ => "core.log.warning",
    }];

    private static string StackLabel(TunEngine value) => Loc.Instance[value switch
    {
        TunEngine.Gvisor => "core.stack.gvisor",
        TunEngine.Mixed => "core.stack.mixed",
        _ => "core.stack.system",
    }];

    private static string MtuLabel(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? Loc.Instance["core.mtu.auto"];

    // Имена отпечатков — как их пишут в ссылках (fp=chrome): так их узнают и ищут.
    private static string FingerprintLabel(TlsFingerprint value) => value == TlsFingerprint.Profile
        ? Loc.Instance["core.fp.profile"]
        : value.ToString().ToLowerInvariant();

    private static string TimeoutLabel(int seconds) => Loc.Instance.Format("core.timeout.value", seconds);

    private static string ReconnectLabel(ReconnectPolicy value) => Loc.Instance[value switch
    {
        ReconnectPolicy.UpTo5Minutes => "core.reconnect.5min",
        ReconnectPolicy.Never => "core.reconnect.never",
        _ => "core.reconnect.backoff",
    }];
}

/// <summary>Лист «Свой MTU»: число от 1280 до 1500.</summary>
public sealed partial class MtuViewModel : ObservableObject
{
    private readonly CorePageViewModel _page;
    private readonly OverlayViewModel _overlay;

    public MtuViewModel(int? current, CorePageViewModel page, OverlayViewModel overlay)
    {
        _page = page;
        _overlay = overlay;
        Value = current?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Value { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private void Save()
    {
        if (!int.TryParse(Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mtu)
            || mtu < ConnectionSettings.MinMtu || mtu > ConnectionSettings.MaxMtu)
        {
            Error = Loc.Instance["core.mtuCustom.bad"];
            return;
        }

        _overlay.CloseSheet();
        _page.SaveMtu(mtu);
    }

    [RelayCommand]
    private void Cancel() => _overlay.CloseSheet();
}
