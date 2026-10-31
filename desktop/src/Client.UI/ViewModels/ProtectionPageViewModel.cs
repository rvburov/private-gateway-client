using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Защита и утечки» по макету (доска 5): блок при обрыве, DNS, IPv6, локальная сеть. Сохраняется
/// сразу; то, что ослабляет защиту, — с вопросом (выключить блок, открыть интернет для входа в
/// Wi-Fi), строгий режим — тоже с вопросом: без VPN сети не будет совсем.
/// </summary>
public sealed partial class ProtectionPageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IGatewayClient _gateway;
    private readonly ConnectionController _controller;
    private readonly OverlayViewModel _overlay;
    private readonly ToastViewModel _toast;
    private readonly INavigator _navigator;
    private readonly IUrlLauncher _launcher;

    public ProtectionPageViewModel(
        SettingsService settings,
        IGatewayClient gateway,
        ConnectionController controller,
        OverlayViewModel overlay,
        ToastViewModel toast,
        INavigator navigator,
        IUrlLauncher launcher)
    {
        _settings = settings;
        _gateway = gateway;
        _controller = controller;
        _overlay = overlay;
        _toast = toast;
        _navigator = navigator;
        _launcher = launcher;
        DnsOptions = [.. Enum.GetValues<DnsChoice>().Select(v =>
            new ChoiceOptionViewModel(() => DnsLabel(v), () => P.Dns == v, () => ChooseDns(v)))];
        AddressOptions = [.. Enum.GetValues<AddressPreference>().Select(v =>
            new ChoiceOptionViewModel(() => AddressLabel(v), () => P.Addresses == v, () => ChooseAddresses(v)))];
        settings.Changed += (_, _) => Refresh();
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    private ProtectionSettings P => _settings.Current.Protection;

    // ── Блок при обрыве ──────────────────────────────────────────────────────

    /// <summary>В режиме «Только прокси» блока при обрыве нет — переключатель недоступен.</summary>
    public bool CanKillSwitch => _settings.Current.Mode == ConnectionMode.Vpn;

    public bool KillSwitch
    {
        get => _settings.Current.KillSwitch;
        set
        {
            if (value || !_settings.Current.KillSwitch)
            {
                _settings.Update(s => s with { KillSwitch = value });
                return;
            }

            // Выключить — только после «Выключить» в диалоге; до ответа переключатель стоит на месте.
            _ = TurnKillSwitchOffAsync();
        }
    }

    /// <summary>Нажатие на переключатель (<see cref="Controls.ConfirmSwitch"/>): выключение — через вопрос.</summary>
    [RelayCommand]
    private void FlipKillSwitch() => KillSwitch = !KillSwitch;

    public string KillSwitchText => Loc.Instance.Format("prot.ks.text", Loc.Instance[
        OperatingSystem.IsWindows() ? "prot.firewall.windows" : OperatingSystem.IsMacOS() ? "prot.firewall.macos" : "prot.firewall.linux"]);

    public string? KillSwitchNote => CanKillSwitch ? null : Loc.Instance["prot.ks.proxy"];

    public string BootTitle => Loc.Instance.Format("prot.boot.title", AppInfo.OsName);

    public bool BootProtection
    {
        get => P.BootProtection;
        set => Set(p => p with { BootProtection = value });
    }

    /// <summary>Строгий режим — часть блока при обрыве: без него недоступен.</summary>
    public bool CanStrictMode => CanKillSwitch && KillSwitch;

    public bool StrictMode
    {
        get => P.StrictMode;
        set
        {
            if (!value || P.StrictMode)
            {
                Set(p => p with { StrictMode = value });
                return;
            }

            _ = TurnStrictModeOnAsync();
        }
    }

    /// <summary>Нажатие на переключатель: включение — через вопрос.</summary>
    [RelayCommand]
    private void FlipStrictMode() => StrictMode = !StrictMode;

    // ── DNS ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<ChoiceOptionViewModel> DnsOptions { get; }

    [ObservableProperty]
    public partial bool IsDnsOpen { get; set; }

    /// <summary>На кнопке — выбранный вариант, у своего адреса — сам адрес.</summary>
    public string DnsText => P is { Dns: DnsChoice.Custom, CustomDns: { } custom } ? custom : DnsLabel(P.Dns);

    public bool BlockOutsideDns
    {
        get => P.BlockOutsideDns;
        set => Set(p => p with { BlockOutsideDns = value });
    }

    public bool FakeDns
    {
        get => P.FakeDns;
        set => Set(p => p with { FakeDns = value });
    }

    public bool CheckLeaksAfterConnect
    {
        get => P.CheckLeaksAfterConnect;
        set => Set(p => p with { CheckLeaksAfterConnect = value });
    }

    // ── IPv6 и локальная сеть ────────────────────────────────────────────────

    public bool BlockIpv6
    {
        get => P.BlockIpv6;
        set => Set(p => p with { BlockIpv6 = value });
    }

    public IReadOnlyList<ChoiceOptionViewModel> AddressOptions { get; }

    [ObservableProperty]
    public partial bool IsAddressesOpen { get; set; }

    public string AddressesText => AddressLabel(P.Addresses);

    public bool AllowLan
    {
        get => P.AllowLan;
        set => Set(p => p with { AllowLan = value });
    }

    /// <summary>Обнаружение устройств — только в разрешенной локальной сети.</summary>
    public bool LanDiscovery
    {
        get => P.LanDiscovery && P.AllowLan;
        set => Set(p => p with { LanDiscovery = value });
    }

    /// <summary>«Разрешить работу рядом с другим VPN» (решение 507): по умолчанию выключено — рядом с ним защита не включается.</summary>
    public bool AllowOtherVpn
    {
        get => P.AllowOtherVpn;
        set => Set(p => p with { AllowOtherVpn = value });
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    [RelayCommand]
    private void ToggleDns() => IsDnsOpen = !IsDnsOpen;

    [RelayCommand]
    private void ToggleAddresses() => IsAddressesOpen = !IsAddressesOpen;

    /// <summary>
    /// «Вход в Wi-Fi отеля или кафе»: при включенной защите — вопрос, пауза на 5 минут и страница входа
    /// в браузере; без защиты — сразу страница: VPN ей не мешает (<see cref="CaptivePortalFlow"/>).
    /// </summary>
    [RelayCommand]
    private Task OpenCaptivePortalAsync() => CaptivePortalFlow.RunAsync(_gateway, _controller, _overlay, _launcher, _navigator, _toast);

    internal void SaveCustomDns(string address) => Set(p => p with { Dns = DnsChoice.Custom, CustomDns = address });

    private void ChooseDns(DnsChoice value)
    {
        IsDnsOpen = false;
        if (value == DnsChoice.Custom)
        {
            _overlay.ShowSheet(new DnsAddressViewModel(P.CustomDns, this, _overlay));
            return;
        }

        Set(p => p with { Dns = value });
    }

    private void ChooseAddresses(AddressPreference value)
    {
        IsAddressesOpen = false;
        Set(p => p with { Addresses = value });
    }

    private async Task TurnKillSwitchOffAsync()
    {
        if (await SafetyDialogs.ConfirmKillSwitchOffAsync(_overlay).ConfigureAwait(true))
        {
            _settings.Update(s => s with { KillSwitch = false });
        }
    }

    private async Task TurnStrictModeOnAsync()
    {
        if (await SafetyDialogs.ConfirmStrictModeAsync(_overlay).ConfigureAwait(true))
        {
            Set(p => p with { StrictMode = true });
        }
    }

    private void Set(Func<ProtectionSettings, ProtectionSettings> change) =>
        _settings.Update(s => s with { Protection = change(s.Protection) });

    private void Refresh()
    {
        foreach (var option in DnsOptions.Concat(AddressOptions))
        {
            option.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }

    private static string DnsLabel(DnsChoice value) => Loc.Instance[value switch
    {
        DnsChoice.CloudflareDoh => "prot.dns.cloudflare",
        DnsChoice.Quad9Doh => "prot.dns.quad9",
        DnsChoice.Custom => "prot.dns.custom",
        _ => "prot.dns.profile",
    }];

    private static string AddressLabel(AddressPreference value) => Loc.Instance[value switch
    {
        AddressPreference.PreferIpv6 => "prot.addr.ipv6",
        AddressPreference.Ipv4Only => "prot.addr.only4",
        _ => "prot.addr.ipv4",
    }];
}

/// <summary>Лист «Свой адрес DNS»: IP или DoH (https://…), иначе — подсказка, что не так.</summary>
public sealed partial class DnsAddressViewModel : ObservableObject
{
    private readonly ProtectionPageViewModel _page;
    private readonly OverlayViewModel _overlay;

    public DnsAddressViewModel(string? current, ProtectionPageViewModel page, OverlayViewModel overlay)
    {
        _page = page;
        _overlay = overlay;
        Address = current ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private void Save()
    {
        if (DnsAddress.Normalize(Address) is not { } address)
        {
            Error = Loc.Instance["prot.dnsCustom.bad"];
            return;
        }

        _overlay.CloseSheet();
        _page.SaveCustomDns(address);
    }

    [RelayCommand]
    private void Cancel() => _overlay.CloseSheet();
}
