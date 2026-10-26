using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«О программе» (доска 15); снимок — для сверки с docs/design/screens/15-about.png.</summary>
public sealed class AboutPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Shows_the_version_honest_component_states_and_privacy()
    {
        var (window, shell, _) = OpenAbout();

        shell.IsSettings.ShouldBeTrue();
        Text(window, "VersionLine").ShouldBe($"Версия {AppInfo.Version} · {AppInfo.OsVersion} · {AppInfo.Architecture}");
        // На Linux — название дистрибутива: «Ubuntu 24.04 LTS».
        AppInfo.OsVersion.ShouldStartWith(OperatingSystem.IsLinux() ? string.Empty : AppInfo.OsName);
        AppInfo.OsVersion.ShouldNotBeNullOrWhiteSpace();
        Text(window, "ClientVersion").ShouldBe(AppInfo.Version);
        Text(window, "ServiceState").ShouldBe("имитация, демо-режим");
        Text(window, "AboutXray").ShouldBe("не установлено");
        Text(window, "AboutSecondCore").ShouldBe("не выбрано");
        Text(window, "TunDriver").ShouldBe(OperatingSystem.IsWindows() ? "не установлен" : "встроен в ОС");
        Text(window, "ChannelText").ShouldBe("Стабильный");
        Snapshot(window, "about");
    }

    [AvaloniaFact]
    public void Update_channel_is_saved()
    {
        var (window, _, page) = OpenAbout();

        Press(window, "ChannelChoice");
        page.IsChannelOpen.ShouldBeTrue();
        page.ChannelOptions.Select(o => o.Label).ShouldBe(["Стабильный", "Бета"]);
        page.ChannelOptions[1].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.IsChannelOpen.ShouldBeFalse();
        Text(window, "ChannelText").ShouldBe("Бета");
        Service<SettingsService>().Current.UpdateChannel.ShouldBe(UpdateChannel.Beta);
    }

    [AvaloniaFact]
    public void Links_open_licenses_privacy_and_the_check_screen()
    {
        var (window, shell, _) = OpenAbout();

        Press(window, "LicensesLink");
        var licenses = shell.Overlay.Sheet.ShouldBeOfType<AboutSheetViewModel>();
        licenses.Title.ShouldBe("Лицензии открытых компонентов");
        licenses.Components.ShouldContain(c => c.Name == "Avalonia" && c.Label == "MIT");
        licenses.Components.ShouldContain(c => c.Name == "SQLite" && c.Label == "Public domain", "непонятный людям SPDX — подписью");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "about-licenses");
        licenses.CloseCommand.Execute(null);

        Press(window, "PrivacyLink");
        var privacy = shell.Overlay.Sheet.ShouldBeOfType<AboutSheetViewModel>();
        (privacy.Title, privacy.HasComponents).ShouldBe(("Политика конфиденциальности", false));
        privacy.Text.ShouldContain("не собирает телеметрию");
        privacy.CloseCommand.Execute(null);

        Press(window, "ReportLink");
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Check, "отчет для поддержки — на экране «Проверка»");
    }

    [Fact]
    public void License_list_covers_every_package_the_app_ships()
    {
        // Пакеты, которые подключают сами проекты; пришедшие с ними сверяет со списком сборка выпуска по SBOM (scripts/package.cs).
        var shipped = Directory.GetFiles(Path.Combine(Repo.Root, "desktop", "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Where(r => (string?)r.Attribute("PrivateAssets") != "all")
            .Select(r => (string)r.Attribute("Include")!)
            .ToHashSet();

        shipped.Where(p => !AboutPageViewModel.Components.Any(c => c.Covers(p))).ShouldBeEmpty("пакет в программе есть, а его лицензии на экране нет");
        AboutPageViewModel.Components.Where(c => c.Packages.Count == 0 && c.Files.Count == 0 && c.Core is null && c.Data is null)
            .Select(c => c.Name).ShouldBeEmpty("компонент ни к чему в программе не привязан");
    }

    [Fact]
    public void License_list_covers_every_font_the_window_carries()
    {
        var src = Path.Combine(Repo.Root, "desktop", "src");
        var fonts = Directory.GetFiles(Path.Combine(src, "Client.UI", "Assets"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        fonts.ShouldNotBeEmpty();
        fonts.Where(f => !AboutPageViewModel.Components.Any(c => c.Files.Any(pattern => Matches(pattern, f))))
            .ShouldBeEmpty("шрифт в окне есть, а его лицензии на экране нет");
    }

    [Fact]
    public void Every_license_has_its_full_text_and_every_component_its_copyright()
    {
        var texts = Path.Combine(Repo.Root, "shared", "licenses", "texts");
        var used = AboutPageViewModel.Components.SelectMany(c => LicenseIds(c.License)).Distinct().ToList();

        used.Where(id => !File.Exists(Path.Combine(texts, TextName(id)))).ShouldBeEmpty("лицензия без полного текста в shared/licenses/texts");
        Directory.GetFiles(texts).Select(Path.GetFileName).Except(used.Select(TextName)).ShouldBeEmpty("текст лицензии, которой ни у кого нет");
        AboutPageViewModel.Components.Where(c => string.IsNullOrWhiteSpace(c.Copyright) || !c.Url.StartsWith("https://", StringComparison.Ordinal))
            .Select(c => c.Name).ShouldBeEmpty("у компонента нет строки авторства или ссылки — THIRD-PARTY-NOTICES без них неполный");
    }

    [Fact]
    public void License_list_covers_every_core_the_service_runs()
    {
        // Ядро — то, у чего в shared/cores закреплена версия: xray.version, sing-box.version.
        var cores = Directory.GetFiles(Path.Combine(Repo.Root, "shared", "cores"), "*.version").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

        cores.ShouldNotBeEmpty();
        cores.Where(core => !AboutPageViewModel.Components.Any(c => c.Core == core)).ShouldBeEmpty("ядро в службе есть, а его лицензии на экране нет");
        AboutPageViewModel.Components.Single(c => c.Core == "sing-box").License.ShouldBe("GPL-3.0-or-later");
    }

    // Выражение SPDX → идентификаторы: «MIT OR Apache-2.0» → MIT, Apache-2.0.
    private static IEnumerable<string> LicenseIds(string expression) =>
        expression.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("OR" or "AND" or "WITH"));

    // Текст GPL один на «-only» и «-or-later»: разница — в строке разрешения, а не в тексте.
    private static string TextName(string id) => id.Replace("-only", string.Empty, StringComparison.Ordinal).Replace("-or-later", string.Empty, StringComparison.Ordinal) + ".txt";

    private static bool Matches(string pattern, string path) =>
        pattern.EndsWith('*') ? path.StartsWith(pattern[..^1], StringComparison.Ordinal) : path == pattern;

    private (MainWindow Window, ShellViewModel Shell, AboutPageViewModel Page) OpenAbout()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>());
    }
}
