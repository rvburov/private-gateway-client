using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Открытый компонент в программе и его лицензия — строка shared/licenses/components.json: тот же список идет в
/// THIRD-PARTY-NOTICES.txt выпуска (scripts/package.cs).
/// </summary>
/// <param name="Name">Как компонент называется для людей.</param>
/// <param name="License">Лицензия — выражение SPDX.</param>
public sealed record OpenComponent(string Name, string License)
{
    /// <summary>Подпись лицензии для людей, если SPDX непонятен («Public domain»).</summary>
    public string? Shown { get; init; }

    public string Copyright { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    /// <summary>Пакеты NuGet: имя целиком или «*» в конце — любой хвост имени.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>Свои файлы в desktop/src, шаблон со «*» (шрифты).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Ядро службы — имя из shared/cores/&lt;core&gt;.version.</summary>
    public string? Core { get; init; }

    /// <summary>Геобаза рядом с ядром.</summary>
    public string? Data { get; init; }

    /// <summary>Подпись лицензии на экране.</summary>
    public string Label => Shown ?? License;

    /// <summary>Пакет приносит этот компонент.</summary>
    public bool Covers(string package) => Packages.Any(p => p.EndsWith('*')
        ? package.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(p, package, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// «О программе» по макету (доска 15): версия и ОС, обновления и их канал, компоненты, приватность,
/// лицензии, политика и «Сообщить о проблеме». Обновлений пока нет — экран так и говорит.
/// </summary>
public sealed partial class AboutPageViewModel : ObservableObject
{
    // Раньше Components: статические поля и свойства получают значения по порядку в тексте.
    private static readonly JsonSerializerOptions ComponentsJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SettingsService _settings;
    private readonly IGatewayClient _gateway;
    private readonly OverlayViewModel _overlay;
    private readonly INavigator _navigator;
    private readonly IServiceInstaller _installer;
    private ServiceDetails? _details;

    public AboutPageViewModel(SettingsService settings, IGatewayClient gateway, OverlayViewModel overlay, INavigator navigator, IServiceInstaller installer)
    {
        _settings = settings;
        _gateway = gateway;
        _overlay = overlay;
        _navigator = navigator;
        _installer = installer;
        _ = LoadDetailsAsync();
        ChannelOptions = [.. Enum.GetValues<UpdateChannel>().Select(v => new ChoiceOptionViewModel(
            () => ChannelLabel(v), () => _settings.Current.UpdateChannel == v, () =>
            {
                IsChannelOpen = false;
                _settings.Update(s => s with { UpdateChannel = v });
            }))];
        settings.Changed += (_, _) => Refresh();
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
    }

    /// <summary>
    /// Что программа приносит с собой: пакеты, свои шрифты, ядра службы (отдельные процессы: Xray-core — прокси и основное
    /// ядро VPN, sing-box — TUN режима VPN; GPL-3.0 — только отдельным процессом) и их геобазы.
    /// </summary>
    public static IReadOnlyList<OpenComponent> Components { get; } = LoadComponents();

    /// <summary>«Версия 0.1 · Windows 11 · x64».</summary>
    public string VersionLine => Loc.Instance.Format("about.version", AppInfo.Version, AppInfo.OsVersion, AppInfo.Architecture);

    public IReadOnlyList<ChoiceOptionViewModel> ChannelOptions { get; }

    [ObservableProperty]
    public partial bool IsChannelOpen { get; set; }

    public string ChannelText => ChannelLabel(_settings.Current.UpdateChannel);

    // ── Компоненты ───────────────────────────────────────────────────────────

    public string ClientVersion => AppInfo.Version;

    /// <summary>Какая служба отвечает окну (установленная, пользователя, никакой, имитация) и ее версия.</summary>
    public string ServiceState => _details is { } details && _gateway.Service != ServiceKind.Simulated
        ? Loc.Instance.Format("about.service.version", ServiceTexts.State(_gateway.Service), details.Version)
        : ServiceTexts.State(_gateway.Service);

    /// <summary>Версии ядер — то, что служба нашла рядом с собой и запустила.</summary>
    public string XrayVersion => _details?.Core("xray")?.Version ?? Loc.Instance["core.version.none"];

    /// <summary>Второе ядро для Hysteria2 и TUIC пока не выбрано: sing-box в программе — движок TUN режима VPN.</summary>
    public string SecondCore => Loc.Instance["core.second.none"];

    /// <summary>Служба другой версии, чем окно: программу обновили, а службу нет (или наоборот).</summary>
    public ServiceMatch ServiceMatch => _details is { } details ? ServiceVersions.Compare(AppInfo.Version, details, ContractMapping.ApiVersion) : ServiceMatch.Same;

    public bool HasServiceNotice => ServiceMatch != ServiceMatch.Same || UpdateFailed;

    public string ServiceNotice => UpdateFailed ? Loc.Instance["about.service.updateFailed"] : ServiceMatch switch
    {
        ServiceMatch.Older => Loc.Instance.Format("about.service.older", _details!.Version, AppInfo.Version),
        ServiceMatch.Newer => Loc.Instance.Format("about.service.newer", _details!.Version, AppInfo.Version),
        ServiceMatch.Incompatible => Loc.Instance.Format("about.service.incompatible", _details!.Version, AppInfo.Version),
        _ => string.Empty,
    };

    /// <summary>Переустановить службу из этой копии программы — та же установка, что «Установить службу».</summary>
    public bool CanUpdateService => ServiceMatch != ServiceMatch.Same && _installer.IsAvailable && _gateway.Service == ServiceKind.Installed && !IsUpdatingService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateService))]
    public partial bool IsUpdatingService { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServiceNotice), nameof(ServiceNotice))]
    public partial bool UpdateFailed { get; set; }

    /// <summary>
    /// TUN режима VPN — sing-box (его версия от службы) поверх драйвера: на Windows — Wintun, его ставит служба; на macOS
    /// и Linux TUN есть в самой ОС.
    /// </summary>
    public string TunDriver
    {
        get
        {
            var driver = Loc.Instance[OperatingSystem.IsWindows() ? "about.tun.none" : "about.tun.system"];
            return _details?.Core("sing-box") is { } engine ? Loc.Instance.Format("about.tun.engine", engine.Version, driver) : driver;
        }
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    [RelayCommand]
    private void ToggleChannel() => IsChannelOpen = !IsChannelOpen;

    [RelayCommand]
    private void OpenLicenses() => _overlay.ShowSheet(new AboutSheetViewModel(
        Loc.Instance["about.licenses"], Loc.Instance["about.licenses.text"], Components, _overlay));

    [RelayCommand]
    private void OpenPrivacy() => _overlay.ShowSheet(new AboutSheetViewModel(
        Loc.Instance["about.privacy"], Loc.Instance["about.privacy.text"], [], _overlay));

    /// <summary>Проблема — на экран «Проверка»: там обезличенный отчет для поддержки.</summary>
    [RelayCommand]
    private void ReportProblem() => _navigator.Navigate(Pages.Check);

    [RelayCommand]
    private async Task UpdateServiceAsync()
    {
        IsUpdatingService = true;
        UpdateFailed = false;
        try
        {
            var result = await _installer.InstallAsync().ConfigureAwait(true);
            UpdateFailed = result == ServiceInstallResult.Failed;
            await LoadDetailsAsync().ConfigureAwait(true);
        }
        finally
        {
            IsUpdatingService = false;
        }
    }

    /// <summary>Сведения службы: версия и ядра; не ответила — строки «не установлено», как без службы.</summary>
    private async Task LoadDetailsAsync()
    {
        _details = await _gateway.GetDetailsAsync().ConfigureAwait(true);
        OnPropertyChanged(string.Empty);
    }

    private void Refresh()
    {
        foreach (var option in ChannelOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }

    private static string ChannelLabel(UpdateChannel channel) =>
        Loc.Instance[channel == UpdateChannel.Beta ? "about.channel.beta" : "about.channel.stable"];

    private static List<OpenComponent> LoadComponents()
    {
        using var stream = typeof(AboutPageViewModel).Assembly.GetManifestResourceStream("open-components.json")
            ?? throw new InvalidOperationException("Нет встроенного open-components.json");
        return JsonSerializer.Deserialize<List<OpenComponent>>(stream, ComponentsJson) ?? [];
    }
}

/// <summary>Лист «О программе»: текст и, у лицензий, список компонентов.</summary>
public sealed partial class AboutSheetViewModel(string title, string text, IReadOnlyList<OpenComponent> components, OverlayViewModel overlay)
    : ObservableObject
{
    public string Title => title;

    public string Text => text;

    public IReadOnlyList<OpenComponent> Components => components;

    public bool HasComponents => components.Count > 0;

    [RelayCommand]
    private void Close() => overlay.CloseSheet();
}
