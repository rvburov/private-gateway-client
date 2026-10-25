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
        licenses.Components.ShouldContain(c => c.Name == "Avalonia" && c.License == "MIT");
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
        var shipped = Directory.GetFiles(Path.Combine(Repo.Root, "desktop", "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Where(r => (string?)r.Attribute("PrivateAssets") != "all")
            .Select(r => (string)r.Attribute("Include")!)
            .ToHashSet();
        var listed = AboutPageViewModel.Components.SelectMany(c => c.Packages).ToHashSet();

        shipped.Except(listed).ShouldBeEmpty("пакет в программе есть, а его лицензии на экране нет");
        listed.Except(shipped).ShouldBeEmpty("на экране — пакет, которого в программе нет");
    }

    private (MainWindow Window, ShellViewModel Shell, AboutPageViewModel Page) OpenAbout()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>());
    }
}
