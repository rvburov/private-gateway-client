using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Открытый компонент в программе и его лицензия.</summary>
/// <param name="Name">Как компонент называется для людей.</param>
/// <param name="License">Лицензия — идентификатор SPDX.</param>
/// <param name="Packages">Пакеты NuGet, которые он приносит, — по ним тест сверяет список с проектами.</param>
public sealed record OpenComponent(string Name, string License, params string[] Packages);

/// <summary>
/// «О программе» по макету (доска 15): версия и ОС, обновления и их канал, компоненты, приватность,
/// лицензии, политика и «Сообщить о проблеме». Обновлений пока нет — экран так и говорит.
/// </summary>
public sealed partial class AboutPageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IGatewayClient _gateway;
    private readonly OverlayViewModel _overlay;
    private readonly INavigator _navigator;

    public AboutPageViewModel(SettingsService settings, IGatewayClient gateway, OverlayViewModel overlay, INavigator navigator)
    {
        _settings = settings;
        _gateway = gateway;
        _overlay = overlay;
        _navigator = navigator;
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
    /// Что программа приносит с собой. Ядра — отдельные процессы службы, без пакетов: Xray-core (прокси и
    /// основное ядро VPN) и sing-box (TUN режима VPN; GPL-3.0 — только отдельным процессом).
    /// </summary>
    public static IReadOnlyList<OpenComponent> Components { get; } =
    [
        new(".NET", "MIT"),
        new("Xray-core", "MPL-2.0"),
        new("sing-box", "GPL-3.0"),
        new("Avalonia", "MIT", "Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent"),
        new("Inter", "OFL-1.1", "Avalonia.Fonts.Inter"),
        new("CommunityToolkit.Mvvm", "MIT", "CommunityToolkit.Mvvm"),
        new("Microsoft.Extensions", "MIT",
            "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.Hosting.WindowsServices"),
        new("Microsoft.Data.Sqlite", "MIT", "Microsoft.Data.Sqlite"),
        new("SQLite", "Public domain"),
        new("SQLitePCLRaw", "Apache-2.0"),
        new("System.Security.Cryptography.ProtectedData", "MIT", "System.Security.Cryptography.ProtectedData"),
        new("System.Management", "MIT", "System.Management"),
        new("Serilog", "Apache-2.0", "Serilog", "Serilog.Sinks.File", "Serilog.Extensions.Logging"),
        new("Protocol Buffers", "BSD-3-Clause", "Google.Protobuf"),
        new("gRPC", "Apache-2.0", "Grpc.Core.Api", "Grpc.Net.Client", "Grpc.AspNetCore.Server"),
        new("YamlDotNet", "MIT", "YamlDotNet"),
        new("ZXing.Net", "Apache-2.0", "ZXing.Net"),
        new("Tmds.DBus", "MIT", "Tmds.DBus.Protocol"),
    ];

    /// <summary>«Версия 0.1 · Windows 11 · x64».</summary>
    public string VersionLine => Loc.Instance.Format("about.version", AppInfo.Version, AppInfo.OsVersion, AppInfo.Architecture);

    public IReadOnlyList<ChoiceOptionViewModel> ChannelOptions { get; }

    [ObservableProperty]
    public partial bool IsChannelOpen { get; set; }

    public string ChannelText => ChannelLabel(_settings.Current.UpdateChannel);

    // ── Компоненты ───────────────────────────────────────────────────────────

    public string ClientVersion => AppInfo.Version;

    /// <summary>Какая служба отвечает окну: установленная, пользователя (только прокси), никакой, имитация.</summary>
    public string ServiceState => ServiceTexts.State(_gateway.Service);

    public string XrayVersion => Loc.Instance["core.version.none"];

    public string SecondCore => Loc.Instance["core.second.none"];

    /// <summary>На Windows TUN дает драйвер Wintun — его ставит служба; на macOS и Linux TUN есть в самой ОС.</summary>
    public string TunDriver => Loc.Instance[OperatingSystem.IsWindows() ? "about.tun.none" : "about.tun.system"];

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
