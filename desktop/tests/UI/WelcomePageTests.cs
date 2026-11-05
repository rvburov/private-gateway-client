using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
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
    public void Fresh_installed_service_without_vpn_servers_can_open_the_existing_mode_settings()
    {
        var (window, shell, page) = OpenWelcome(ServiceKind.Installed);
        Press(window, "StartButton");
        page.IsAddServer.ShouldBeTrue();
        Service<IServerStore>().Servers.ShouldBeEmpty();

        Press(window, "OpenConnectionSettings");
        shell.CurrentKey.ShouldBe(Pages.Settings);
        var settings = shell.CurrentPage.ShouldBeOfType<SettingsPageViewModel>();
        settings.IsVpnMode.ShouldBeTrue("открытие настроек само не меняет режим подключения");
        settings.ChooseProxyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Vpn);
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        settings.IsProxyMode.ShouldBeTrue();
        Find<Control>(window, "ExternalProxyForm").IsVisible.ShouldBeTrue();
        Service<IServerStore>().Servers.ShouldBeEmpty("для ввода внешнего прокси VPS не нужен");
        shell.Protection.Status.State.ShouldBe(ConnectionState.Off);
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

    [AvaloniaFact]
    public void Service_step_installs_the_service_and_goes_on_to_the_first_server()
    {
        Installer.IsAvailable = true;
        var (window, _, page) = OpenWelcome(ServiceKind.None);

        Press(window, "StartButton");
        Dispatcher.UIThread.RunJobs();

        Find<Grid>(window, "ServiceStep").IsVisible.ShouldBeTrue("службы нет, а поставить ее можно — шаг 1 из 2");
        Text(window, "ServiceTitle").ShouldBe("Установка службы");
        page.Rows.Select(r => $"{r.Title}: {r.Detail}").ShouldBe(
            ["Служба Private Gateway: Управляет VPN в фоне", "Сетевой адаптер (TUN): Появятся при подключении", "Правила брандмауэра: Появятся при подключении"]);
        Find<Button>(window, "InstallService").Content.ShouldBe("Установить");
        Snapshot(window, "welcome-service");

        Press(window, "InstallService");
        Dispatcher.UIThread.RunJobs();

        Installer.Installs.ShouldBe(1);
        page.Rows.Select(r => (r.Detail, r.State)).ShouldBe([("Готово", SetupRowState.Done),
            ("Появятся при подключении", SetupRowState.Pending), ("Появятся при подключении", SetupRowState.Pending)],
            "ставится служба; адаптер и правила появятся при подключении — не «готово»");
        (Find<Button>(window, "InstallService").Content, Find<Button>(window, "WithoutService").IsVisible).ShouldBe(("Продолжить", false));

        Press(window, "InstallService");
        Dispatcher.UIThread.RunJobs();

        Find<Grid>(window, "AddServerStep").IsVisible.ShouldBeTrue();
        Text(window, "ServiceReady").ShouldBe("Служба установлена · блок при обрыве включится при первом подключении");
        Find<TextBlock>(window, "ServiceReady").IsVisible.ShouldBeTrue();
        Service<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Vpn);
    }

    [AvaloniaFact]
    public void Refused_rights_keep_the_step_and_proxy_mode_skips_it()
    {
        Installer.IsAvailable = true;
        Installer.Result = ServiceInstallResult.Cancelled;
        var (window, _, page) = OpenWelcome(ServiceKind.None);
        Press(window, "StartButton");
        Dispatcher.UIThread.RunJobs();

        Press(window, "InstallService");
        Dispatcher.UIThread.RunJobs();

        Text(window, "InstallError").ShouldBe("Установка отменена: права администратора не даны");
        (page.IsService, Find<Button>(window, "InstallService").Content).ShouldBe((true, "Установить"));

        // Установка не вышла и назвала причину (окно ОС с правами свой вывод не показывает — причина из файла итога).
        Installer.Result = ServiceInstallResult.Failed;
        Installer.LastError = "служба работает от имени системы, а ей не виден .NET 10";
        Press(window, "InstallService");
        Dispatcher.UIThread.RunJobs();
        Text(window, "InstallError").ShouldBe("Служба не установлена: служба работает от имени системы, а ей не виден .NET 10");

        Press(window, "WithoutService");
        Dispatcher.UIThread.RunJobs();

        Service<ShellViewModel>().CurrentKey.ShouldBe(Pages.Settings, "внешнему прокси не нужен первый VPN-сервер");
        Service<ShellViewModel>().CurrentPage.ShouldBeOfType<SettingsPageViewModel>().IsProxyMode.ShouldBeTrue();
        Service<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Proxy, "без службы — только режим прокси");
    }

    [AvaloniaFact]
    public void Installation_shows_reported_progress_without_claiming_the_tunnel_is_installed()
    {
        Installer.IsAvailable = true;
        Installer.Pending = new TaskCompletionSource<ServiceInstallResult>();
        var (window, _, page) = OpenWelcome(ServiceKind.None);
        Press(window, "StartButton");
        Press(window, "InstallService");
        Installer.Progress.ShouldNotBeNull().Report(60);
        Dispatcher.UIThread.RunJobs();
        page.Rows[0].Percent.ShouldBe(60);
        page.Rows[0].Detail.ShouldContain("60%");
        page.Rows[0].ShowProgress.ShouldBeTrue();
        page.Rows.Skip(1).ShouldAllBe(row => !row.ShowProgress && row.State == SetupRowState.Pending);
        Find<Button>(window, "InstallService").IsEffectivelyEnabled.ShouldBeFalse();
        Snapshot(window, "welcome-service-progress");
        Installer.Pending.SetResult(ServiceInstallResult.Failed);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTheory]
    [InlineData(ServiceKind.Installed, true)]
    [InlineData(ServiceKind.None, false)]
    public void Service_step_is_skipped_when_there_is_nothing_to_install(ServiceKind service, bool installerAvailable)
    {
        Installer.IsAvailable = installerAvailable;
        var (window, _, page) = OpenWelcome(service);

        Press(window, "StartButton");
        Dispatcher.UIThread.RunJobs();

        (page.IsService, page.IsAddServer).ShouldBe((false, true), "служба уже стоит или поставить ее нельзя (macOS) — сразу к первому серверу");
    }

    private (MainWindow Window, ShellViewModel Shell, WelcomePageViewModel Page) OpenWelcome(ServiceKind? service = null)
    {
        var (window, shell) = service is { } kind
            ? Open(firstLaunch: true, gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, kind))
            : Open(firstLaunch: true);
        return (window, shell, shell.CurrentPage.ShouldBeOfType<WelcomePageViewModel>());
    }
}
