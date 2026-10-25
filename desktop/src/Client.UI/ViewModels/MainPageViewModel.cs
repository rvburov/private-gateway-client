using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Главный экран по макету (docs/design, экран 1): карточка состояния с четырьмя проверками,
/// карточка «Сайты видят ваш IP» с выбранным сервером, скорость. Все тексты — из состояния,
/// которое прислала служба.
/// </summary>
public sealed partial class MainPageViewModel : ObservableObject, IDisposable
{
    private const string IpPlaceholder = "—";

    private readonly IGatewayClient _gateway;
    private readonly IServerCatalog _catalog;
    private readonly ConnectionController _controller;
    private readonly TimeProvider _time;
    private readonly IClipboardService _clipboard;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _clock;

    public MainPageViewModel(
        ProtectionViewModel protection,
        IGatewayClient gateway,
        IServerCatalog catalog,
        ConnectionController controller,
        TimeProvider time,
        IClipboardService clipboard,
        INavigator navigator,
        ToastViewModel toast,
        SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(protection);
        Protection = protection;
        _gateway = gateway;
        _catalog = catalog;
        _controller = controller;
        _time = time;
        _clipboard = clipboard;
        _navigator = navigator;
        _toast = toast;
        _settings = settings;
        Chips = [new("all"), new("dns"), new("ipv6"), new("ks")];
        foreach (var chip in Chips)
        {
            chip.PropertyChanged += OnChipChanged;
        }

        Protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                if (Protection.Status.Protection.CheckedAt != _lastCheckedAt)
                {
                    IsChecking = false;
                }

                Refresh();
            }
        };
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        settings.Changed += (_, _) => Refresh();
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) =>
            {
                OnPropertyChanged(nameof(TimerText));
                OnPropertyChanged(nameof(PauseLeft));
            });
        _clock.Start();
        Refresh();
    }

    private DateTimeOffset? _lastCheckedAt;

    public ProtectionViewModel Protection { get; }

    private ConnectionStatus Status => Protection.Status;

    public bool IsDemo => _gateway.IsSimulated;

    public string Version => AppInfo.Version;

    // ── Карточка состояния ───────────────────────────────────────────────────

    public Tone Tone => Protection.Tone;

    public string StatusTitle => Loc.Instance[$"status.{StateKey}.title"];

    public string StatusSubtitle => Status.IsPaused ? Loc.Instance["status.paused.sub"]
        : Status.IsProxy ? Loc.Instance["status.proxy.sub"]
        : Status.State switch
    {
        ConnectionState.Connected => _catalog.Selected is { } server && !ServerFacts.IsEncrypted(server)
            ? Loc.Instance["status.connected.subPlain"]
            : Loc.Instance["status.connected.sub"],
        ConnectionState.Reconnecting => Loc.Instance.Format("status.reconnecting.sub", Status.Attempt),
        ConnectionState.Error => Loc.Instance.Format("status.error.sub", Status.ErrorCode ?? "PGC-SVC"),
        _ => Loc.Instance[$"status.{StateKey}.sub"],
    };

    public string NetworkText => Status.Network switch
    {
        { Kind: NetworkKind.WiFi, Name: null or "" } => Loc.Instance["network.wifiNoName"],
        { Kind: NetworkKind.WiFi, IsOpen: true } n => Loc.Instance.Format("network.wifiOpen", Cut(n.Name!, 20)),
        { Kind: NetworkKind.WiFi } n => Loc.Instance.Format("network.wifi", Cut(n.Name!, 20)),
        { Kind: NetworkKind.Ethernet } => Loc.Instance["network.ethernet"],
        { Kind: NetworkKind.UsbModem } => Loc.Instance["network.usb"],
        { Kind: NetworkKind.Mobile } => Loc.Instance["network.mobile"],
        _ => Loc.Instance["network.none"],
    };

    /// <summary>Ключ иконки сети: Icon.Net.*.</summary>
    public string NetworkIcon => "Icon.Net." + Status.Network.Kind switch
    {
        NetworkKind.WiFi => "WiFi",
        NetworkKind.Ethernet => "Ethernet",
        NetworkKind.UsbModem => "Usb",
        NetworkKind.Mobile => "Mobile",
        _ => "None",
    };

    public bool HasTimer => Status.State == ConnectionState.Connected && !Status.IsPaused;

    /// <summary>Пауза для входа в Wi-Fi: полоса с обратным отсчетом и «Вернуть защиту» в карточке состояния.</summary>
    public bool IsPaused => Status.IsPaused;

    /// <summary>
    /// Раздельный режим при включенной защите: полоса «…идут мимо VPN» — «Защищено» не должно скрывать,
    /// что часть трафика идет мимо туннеля. Во время паузы мимо VPN и так все — там своя полоса.
    /// </summary>
    public bool HasBypass => BypassKey is not null;

    public string? BypassText => BypassKey is { } key ? Loc.Instance[key] : null;

    private string? BypassKey
    {
        get
        {
            var routing = _settings.Current.Routing;
            if (Status.State == ConnectionState.Off || Status.IsPaused || _settings.Current.Mode != ConnectionMode.Vpn)
            {
                return null;
            }

            return (routing.HasBypassApps, routing.HasBypassSites) switch
            {
                (true, true) => "main.bypass.both",
                (true, false) => "main.bypass.apps",
                (false, true) => "main.bypass.sites",
                _ => null,
            };
        }
    }

    /// <summary>Сколько осталось паузы: «4:59».</summary>
    public string PauseLeft
    {
        get
        {
            var left = (Status.CaptivePauseUntil ?? _time.GetUtcNow()) - _time.GetUtcNow();
            if (left < TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
            }

            return string.Create(CultureInfo.InvariantCulture, $"{(int)left.TotalMinutes}:{left.Seconds:00}");
        }
    }

    /// <summary>Сколько в сети: «02:14:09».</summary>
    public string TimerText
    {
        get
        {
            var span = _time.GetUtcNow() - Status.Since;
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            return string.Create(CultureInfo.InvariantCulture,
                $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}");
        }
    }

    public ObservableCollection<CheckChipViewModel> Chips { get; }

    public CheckChipViewModel? SelectedChip => Chips.FirstOrDefault(c => c.IsSelected);

    public bool HasChipInfo => SelectedChip is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckedText), nameof(HasCheckAction))]
    public partial bool IsChecking { get; private set; }

    public string CheckedText
    {
        get
        {
            if (Status.State != ConnectionState.Connected || Status.IsPaused)
            {
                return Loc.Instance["checked." + StateKey];
            }

            if (IsChecking)
            {
                return Loc.Instance["checked.running"];
            }

            // В режиме прокси главное, что надо знать, — блока при обрыве нет (доска 17).
            if (Status.IsProxy)
            {
                return Loc.Instance["checked.proxy"];
            }

            var at = Status.Protection.CheckedAt;
            if (at is null)
            {
                return Loc.Instance["checked.connecting"];
            }

            return _time.GetUtcNow() - at.Value < TimeSpan.FromSeconds(10)
                ? Loc.Instance["checked.now"]
                : Loc.Instance.Format("checked.at", at.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Кнопка справа от строки проверки: «Проверить», «Повторить», «Перезапустить службу».</summary>
    public string CheckActionText => Status.State switch
    {
        ConnectionState.Blocked => Loc.Instance["action.retry"],
        ConnectionState.Error => Loc.Instance["action.restartService"],
        _ => Loc.Instance["action.check"],
    };

    public bool HasCheckAction => !Status.IsPaused && Status.State switch
    {
        ConnectionState.Connected => !IsChecking,
        ConnectionState.Blocked or ConnectionState.Error => true,
        _ => false,
    };

    public Tone CheckActionTone => Status.State switch
    {
        ConnectionState.Blocked => Tone.Blocked,
        ConnectionState.Error => Tone.Error,
        _ => Tone.Connecting,
    };

    // ── IP и сервер ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebIp), nameof(MaskIcon), nameof(MaskHint))]
    public partial bool IsIpMasked { get; private set; }

    /// <summary>IP, который видят сайты: в туннеле — выходной сервера, без защиты — реальный.</summary>
    public string WebIp => Mask(Status.State switch
    {
        _ when Status.IsPaused => Status.Exit.DirectIp ?? IpPlaceholder,
        ConnectionState.Connected => Status.Exit.Ip ?? IpPlaceholder,
        ConnectionState.Off => Status.Exit.DirectIp ?? IpPlaceholder,
        _ => IpPlaceholder,
    });

    public bool CanCopyIp => WebIp != IpPlaceholder && !IsIpMasked;

    public string WebIpMeta => Status.IsPaused ? Loc.Instance["ip.meta.paused"] : Status.State switch
    {
        ConnectionState.Connected when Status.IsProxy => string.Join(" · ",
            new[] { Status.Exit.City, Loc.Instance["ip.meta.proxy"] }.Where(s => !string.IsNullOrEmpty(s))),
        ConnectionState.Connected => string.Join(" · ",
            new[] { Status.Exit.City, Status.Exit.NetworkType, Status.Exit.Asn }.Where(s => !string.IsNullOrEmpty(s))),
        ConnectionState.Off => Loc.Instance["ip.meta.off"],
        ConnectionState.Blocked or ConnectionState.Error => Loc.Instance["ip.meta.stopped"],
        _ => Loc.Instance["ip.meta.waiting"],
    };

    public bool HasRealIp => Status.Exit.DirectIp is not null;

    public string RealIp => Mask(Status.Exit.DirectIp ?? IpPlaceholder);

    public string RealIpState => Status.State == ConnectionState.Off || Status.IsPaused
        ? Loc.Instance["ip.real.visible"]
        : Loc.Instance["ip.real.hidden"];

    public string MaskIcon => IsIpMasked ? "Icon.EyeOff" : "Icon.Eye";

    public string MaskHint => IsIpMasked ? Loc.Instance["ip.show"] : Loc.Instance["ip.hide"];

    public bool HasServer => _catalog.Selected is not null;

    public string ServerTitle => _catalog.Selected is { } s
        ? (string.IsNullOrEmpty(s.CountryCode) ? s.Name : s.CountryCode + " · " + s.Name)
        : Loc.Instance["server.none"];

    public string ServerMeta => _catalog.Selected is { } s ? Describe(s, withPort: true) : Loc.Instance["server.addFirst"];

    [ObservableProperty]
    public partial bool IsSwitchOpen { get; set; }

    /// <summary>Быстрая смена: текущий сервер и еще два с лучшим пингом.</summary>
    public IReadOnlyList<QuickServerViewModel> QuickServers
    {
        get
        {
            var current = _catalog.Selected;
            var others = _catalog.Servers
                .Where(s => s.Id != current?.Id && s.LatencyMs is not null)
                .OrderBy(s => s.LatencyMs);
            return (current is null ? others : others.Prepend(current))
                .Take(3)
                .Select(s => new QuickServerViewModel(s, s.Id == current?.Id, Describe(s, withPort: false),
                    s.LatencyMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"] : "—",
                    ChooseServerCommand))
                .ToList();
        }
    }

    // ── Скорость ─────────────────────────────────────────────────────────────

    public string PingText => Status.State == ConnectionState.Connected && Status.Traffic.LatencyMs is { } ms
        ? ms.ToString(CultureInfo.InvariantCulture)
        : IpPlaceholder;

    public string DownText => Status.State == ConnectionState.Connected
        ? Status.Traffic.DownloadMbps.ToString("0.0", Loc.Instance.Culture)
        : IpPlaceholder;

    public string UpText => Status.State == ConnectionState.Connected
        ? Status.Traffic.UploadMbps.ToString("0.0", Loc.Instance.Culture)
        : IpPlaceholder;

    public bool IsPingGood => Status.State == ConnectionState.Connected;

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Navigate(string page) => _navigator.Navigate(page);

    [RelayCommand]
    private void ToggleIpMask() => IsIpMasked = !IsIpMasked;

    [RelayCommand]
    private Task ResumeAsync() => _controller.ResumeAsync();

    [RelayCommand]
    private async Task CopyIpAsync()
    {
        if (!CanCopyIp)
        {
            return;
        }

        await _clipboard.SetTextAsync(WebIp).ConfigureAwait(true);
        _toast.Show(Loc.Instance["ip.copied"]);
    }

    [RelayCommand]
    private void ToggleSwitch() => IsSwitchOpen = !IsSwitchOpen;

    [RelayCommand]
    private async Task ChooseServerAsync(string serverId)
    {
        IsSwitchOpen = false;
        await _controller.SwitchServerAsync(serverId).ConfigureAwait(true);
    }

    [RelayCommand]
    private void AllServers()
    {
        IsSwitchOpen = false;
        _navigator.Navigate(Pages.Servers);
    }

    [RelayCommand]
    private async Task RefreshServersAsync()
    {
        await _catalog.RefreshAsync().ConfigureAwait(true);
        _toast.Show(Loc.Instance["server.refreshed"]);
    }

    [RelayCommand]
    private async Task CheckActionAsync()
    {
        switch (Status.State)
        {
            case ConnectionState.Connected:
                _lastCheckedAt = Status.Protection.CheckedAt;
                IsChecking = true;
                await _gateway.RunChecksAsync().ConfigureAwait(true);
                break;
            case ConnectionState.Blocked or ConnectionState.Error when _catalog.Selected is { } server:
                await _gateway.ConnectAsync(server).ConfigureAwait(true);
                break;
        }
    }

    public void Dispose() => _clock.Stop();

    private string StateKey => Status.IsPaused ? "paused" : Status.IsProxy ? "proxy" : Status.State.ToString().ToLowerInvariant();

    private void Refresh()
    {
        var protection = Status.Protection;
        var paused = Status.IsPaused;
        Chips[0].Update(protection.AllTraffic, paused);
        Chips[1].Update(protection.Dns, paused);
        Chips[2].Update(protection.Ipv6, paused);
        Chips[3].Update(protection.KillSwitch, paused);
        OnPropertyChanged(string.Empty);
    }

    private void OnChipChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CheckChipViewModel.IsSelected) || sender is not CheckChipViewModel { IsSelected: true } chip)
        {
            OnPropertyChanged(nameof(SelectedChip));
            OnPropertyChanged(nameof(HasChipInfo));
            return;
        }

        foreach (var other in Chips.Where(c => c != chip))
        {
            other.IsSelected = false;
        }

        OnPropertyChanged(nameof(SelectedChip));
        OnPropertyChanged(nameof(HasChipInfo));
    }

    private string Mask(string ip) => IsIpMasked && ip != IpPlaceholder
        ? string.Concat(ip.Select(c => char.IsAsciiDigit(c) ? '•' : c))
        : ip;

    private static string Describe(ServerProfile server, bool withPort)
    {
        var summary = ServerFacts.Summary(server);
        return withPort ? summary + " · " + Loc.Instance.Format("server.port", server.Port) : summary;
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] + "…" : text;
}

/// <summary>Один из четырех чипов проверки; по нажатию раскрывает пояснение.</summary>
public sealed partial class CheckChipViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Value), nameof(Icon))]
    public partial CheckState State { get; private set; } = CheckState.Off;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Title => Loc.Instance[$"check.{Id}.title"];

    /// <summary>Пауза для входа в Wi-Fi: «напрямую», «у провайдера», «открыт», «на паузе».</summary>
    public bool IsPaused { get; private set; }

    public string Value => Loc.Instance[$"check.{Id}.{(IsPaused ? "paused" : State.ToString().ToLowerInvariant())}"];

    public string Text => Loc.Instance[$"check.{Id}.text"];

    /// <summary>Ключ иконки в кружке: Icon.Chip.*.</summary>
    public string Icon => CheckIcon.For(State);

    internal void Update(CheckState state, bool paused = false)
    {
        IsPaused = paused;
        State = state;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Text));
    }

    [RelayCommand]
    private void Toggle() => IsSelected = !IsSelected;
}

/// <summary>Строка быстрой смены сервера.</summary>
public sealed record QuickServerViewModel(
    ServerProfile Server,
    bool IsCurrent,
    string Meta,
    string LatencyText,
    IAsyncRelayCommand<string> ChooseCommand)
{
    public string Id => Server.Id;

    public string CountryCode => Server.CountryCode;

    public string Name => Server.Name;
}
