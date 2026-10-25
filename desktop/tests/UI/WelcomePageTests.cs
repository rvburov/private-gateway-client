using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Первый запуск (доска 10); снимки — для сверки с docs/design/screens/10-onboarding.png.</summary>
public sealed class WelcomePageTests : WindowHarness
{
    [AvaloniaFact]
    public void First_launch_greets_without_the_island_and_switches_language_at_once()
    {
        var (window, shell, page) = OpenWelcome();

        (shell.CurrentKey, shell.IsIslandVisible).ShouldBe((Pages.Welcome, false));
        Find<Grid>(window, "Greeting").IsVisible.ShouldBeTrue();
        Text(window, "AllTrafficTitle").ShouldBe($"Весь трафик {AppInfo.OsName} через VPN");
        Snapshot(window, "welcome");

        Find<RadioButton>(window, "WelcomeEn").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Loc.Instance.Language.ShouldBe("en");
        Service<SettingsService>().Current.Language.ShouldBe("en");
        Text(window, "AllTrafficTitle").ShouldBe($"All {AppInfo.OsName} traffic through VPN");
        page.IsRussian.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Start_without_servers_offers_the_first_one_or_a_look_at_demo()
    {
        var (window, shell, page) = OpenWelcome();

        Press(window, "StartButton");
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.OnboardingDone.ShouldBeTrue("приветствие больше не покажется");
        (page.IsAddServer, Find<Grid>(window, "Greeting").IsVisible).ShouldBe((true, false));
        Find<Button>(window, "TryDemo").IsVisible.ShouldBeTrue("подключение — имитация");
        Snapshot(window, "welcome-add");

        Press(window, "AddFirstServer");
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Add);
        shell.Back();
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Welcome, "из «Добавить профиль» — назад к первому серверу");
        Find<Grid>(window, "AddServerStep").IsVisible.ShouldBeTrue();

        Press(window, "TryDemo");
        Dispatcher.UIThread.RunJobs();
        (shell.CurrentKey, shell.IsIslandVisible).ShouldBe((Pages.Home, true));
    }

    [AvaloniaFact]
    public void Start_with_own_servers_goes_straight_home()
    {
        var (window, shell, _) = OpenWelcome();
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw@198.51.100.1:443#A").Server!], null);

        Press(window, "StartButton");
        Dispatcher.UIThread.RunJobs();

        shell.CurrentKey.ShouldBe(Pages.Home);
    }

    private (MainWindow Window, ShellViewModel Shell, WelcomePageViewModel Page) OpenWelcome()
    {
        var (window, shell) = Open(firstLaunch: true);
        return (window, shell, shell.CurrentPage.ShouldBeOfType<WelcomePageViewModel>());
    }
}
