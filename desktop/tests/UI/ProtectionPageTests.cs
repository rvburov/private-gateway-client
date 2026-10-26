using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«Защита и утечки» (доска 5); снимок — для сверки с docs/design/screens/05-protection.png.</summary>
public sealed class ProtectionPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Defaults_are_closed_as_on_the_board()
    {
        var (window, shell, page) = OpenProtection();

        shell.IsSettings.ShouldBeTrue();
        shell.IsIslandVisible.ShouldBeTrue();
        (page.KillSwitch, page.BootProtection, page.StrictMode).ShouldBe((true, true, false));
        (page.BlockOutsideDns, page.FakeDns, page.CheckLeaksAfterConnect).ShouldBe((true, false, true));
        (page.BlockIpv6, page.AllowLan, page.LanDiscovery).ShouldBe((true, false, false));
        Text(window, "DnsChoiceText").ShouldBe("Из профиля");
        Text(window, "AddressChoiceText").ShouldBe("Предпочитать IPv4");
        page.KillSwitchText.ShouldStartWith("Обрыв, сбой ядра или службы — весь трафик блокируется ");
        page.KillSwitchText.ShouldEndWith("Исключение: только адрес выбранного сервера.");
        page.BootTitle.ShouldBe("Защита при загрузке " + AppInfo.OsName);
        Find<ToggleButton>(window, "LanDiscoveryToggle").IsEnabled.ShouldBeFalse("без локальной сети обнаружение недоступно");
        Snapshot(window, "protection");
    }

    [AvaloniaFact]
    public void Switches_are_saved_at_once_and_reach_the_service_contract()
    {
        var (window, _, page) = OpenProtection();

        page.FakeDns = true;
        page.BlockIpv6 = false;
        page.AllowLan = true;
        Dispatcher.UIThread.RunJobs();
        Find<ToggleButton>(window, "LanDiscoveryToggle").IsEnabled.ShouldBeTrue();
        page.LanDiscovery = true;

        var saved = Service<SettingsService>().Current.Protection;
        (saved.FakeDns, saved.BlockIpv6, saved.AllowLan, saved.LanDiscovery).ShouldBe((true, false, true, true));

        page.AllowLan = false;
        page.LanDiscovery.ShouldBeFalse("без локальной сети обнаружение выключено");
    }

    [AvaloniaFact]
    public void Dns_is_chosen_from_the_list_or_typed_in()
    {
        var (window, shell, page) = OpenProtection();

        Press(window, "DnsChoice");
        page.IsDnsOpen.ShouldBeTrue();
        page.DnsOptions.Select(o => o.Label).ShouldBe(["Из профиля", "DoH: Cloudflare", "DoH: Quad9", "Свой адрес…"]);
        page.DnsOptions[1].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.IsDnsOpen.ShouldBeFalse();
        Text(window, "DnsChoiceText").ShouldBe("DoH: Cloudflare");
        Service<SettingsService>().Current.Protection.DnsServers.ShouldBe(["https://1.1.1.1/dns-query"]);

        page.DnsOptions[3].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<DnsAddressViewModel>();
        sheet.Address = "1.2";
        sheet.SaveCommand.Execute(null);
        sheet.Error.ShouldBe("Нужен IP-адрес или адрес https://…");
        Service<SettingsService>().Current.Protection.Dns.ShouldBe(DnsChoice.CloudflareDoh, "пока адрес не подошел, выбор прежний");

        sheet.Address = " 192.0.2.53 ";
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        Text(window, "DnsChoiceText").ShouldBe("192.0.2.53");
        page.DnsOptions.Single(o => o.IsCurrent).Label.ShouldBe("Свой адрес…");
    }

    [AvaloniaFact]
    public void Address_priority_is_chosen_from_the_list()
    {
        var (window, _, page) = OpenProtection();

        Press(window, "AddressChoice");
        page.AddressOptions[2].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Text(window, "AddressChoiceText").ShouldBe("Только IPv4");
        Service<SettingsService>().Current.Protection.Addresses.ShouldBe(AddressPreference.Ipv4Only);
    }

    [AvaloniaFact]
    public void Kill_switch_goes_off_only_after_confirmation_and_takes_strict_mode_with_it()
    {
        var (_, shell, page) = OpenProtection();

        page.KillSwitch = false;
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().Title.ShouldBe("Выключить блок при обрыве?");
        page.KillSwitch.ShouldBeTrue();

        shell.Overlay.Dialog.SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.KillSwitch.ShouldBeFalse();
        page.CanStrictMode.ShouldBeFalse("строгий режим — часть блока при обрыве");
        page.KillSwitch = true;
        shell.Overlay.HasDialog.ShouldBeFalse("включить можно без вопроса");
        page.KillSwitch.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Strict_mode_asks_before_turning_on_but_not_off()
    {
        var (_, shell, page) = OpenProtection();

        page.StrictMode = true;
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        dialog.Title.ShouldBe("Включить строгий режим?");
        page.StrictMode.ShouldBeFalse();
        dialog.PrimaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.StrictMode.ShouldBeTrue();

        page.StrictMode = false;
        shell.Overlay.HasDialog.ShouldBeFalse();
        page.StrictMode.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_with_protection_asks_pauses_and_opens_the_page()
    {
        var (window, shell) = Open();
        await Connect(shell);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        dialog.Title.ShouldBe("Открыть интернет без VPN на 5 минут?");
        dialog.PrimaryText.ShouldBe("Оставить защиту");
        dialog.IsSecondaryDanger.ShouldBeTrue();
        dialog.SecondaryCommand.Execute(null);
        await open;
        WaitFor(() => shell.Protection.Status.IsPaused);

        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        shell.CurrentKey.ShouldBe(Pages.Home, "на главной видно отсчет и «Вернуть защиту»");
        shell.Toast.Text.ShouldBe("Пауза на 5 минут: войдите в сеть на открывшейся странице");
        Find<Border>(window, "PauseBanner").IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_dismissed_keeps_protection()
    {
        var (_, shell) = Open();
        await Connect(shell);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.DismissCommand.Execute(null);
        await open;

        shell.Protection.Status.IsPaused.ShouldBeFalse();
        Urls.Opened.ShouldBeEmpty();
        shell.CurrentKey.ShouldBe(Pages.Protection);
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_when_the_service_has_nothing_to_pause_says_so()
    {
        // Режим прокси без системного прокси: служба отвечает, что паузе нечего менять.
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User));
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        await open;

        shell.Protection.Status.IsPaused.ShouldBeFalse();
        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        shell.Toast.Text.ShouldBe("Пауза не нужна: системный прокси выключен — страница входа в сеть откроется напрямую",
            "паузы нет — и окно не делает вид, что она есть");
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_that_the_service_refuses_in_vpn_does_not_open_the_page()
    {
        // Режим VPN: служба отказала (блок не снялся) — сеть закрыта, страница входа не загрузится.
        var service = default(ScriptedService);
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User));
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        await open;

        (service!.Pauses, Urls.Opened.Count).ShouldBe((1, 0));
        shell.Toast.Text.ShouldBe(Loc.Instance["pause.failed"]);
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_while_reconnecting_asks_pauses_and_opens_the_page()
    {
        // Отель: сервер за порталом не отвечает, служба переподключается без конца — пауза останавливает попытки до своего конца.
        var service = default(ScriptedService);
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User)
        {
            GrantsPause = true,
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        service!.Push(s => s.MoveTo(ConnectionState.Reconnecting, Time.GetUtcNow()));
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Reconnecting);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull("в «Переподключении» пауза есть — вопрос, как в «Защищено»").SecondaryCommand.Execute(null);
        await open;
        WaitFor(() => shell.Protection.Status.IsPaused);

        service.Pauses.ShouldBe(1);
        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        (shell.CurrentKey, shell.Toast.Text).ShouldBe((Pages.Home, Loc.Instance["pause.started"]));
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_after_a_proxy_error_pauses_the_dead_system_proxy()
    {
        // Режим прокси, сервер не ответил: «Ошибка», а системный прокси ведет на остановленное ядро — сеть для браузера закрыта.
        var (_, shell) = Open();
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        var dead = Service<IServerCatalog>().Servers.Single(s => s.Name == "Турция 01");
        var connect = Service<ConnectionController>().ConnectToAsync(dead.Id);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Error);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        await open;

        WaitFor(() => shell.Protection.Status.IsPaused);
        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        shell.Toast.Text.ShouldBe("Пауза на 5 минут: войдите в сеть на открывшейся странице");
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_after_an_error_without_a_block_just_opens_the_page()
    {
        var service = default(ScriptedService);
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User)
        {
            // Отказ в подключении: защиты нет, трафик идет напрямую.
            Outcome = c => (c with { ErrorCode = "PGC-NET-003" }).MoveTo(ConnectionState.Error, Time.GetUtcNow()) with
            {
                Protection = ProtectionChecks.For(ConnectionState.Off),
            },
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Error);
        var page = Navigate(shell);

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.HasDialog.ShouldBeFalse("сеть открыта — спрашивать о паузе незачем");
        await open;

        service!.Pauses.ShouldBe(0);
        Urls.Opened.ShouldBe([CaptivePortal.CheckPage], "сеть открыта — страница входа откроется и без паузы");
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_again_during_the_pause_reopens_the_page_without_asking()
    {
        var (_, shell) = Open();
        await Connect(shell);
        var page = Navigate(shell);
        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        await open;
        WaitFor(() => shell.Protection.Status.IsPaused);
        page = Navigate(shell);

        var again = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.HasDialog.ShouldBeFalse("пауза уже идет — спрашивать не о чем");
        await again;

        Urls.Opened.ShouldBe([CaptivePortal.CheckPage, CaptivePortal.CheckPage], "страницу закрыли раньше, чем вошли, — открыть снова");
        shell.Toast.Text.ShouldBe(Loc.Instance["pause.running"]);
    }

    [AvaloniaFact]
    public async Task Wifi_sign_in_without_protection_just_opens_the_page()
    {
        var (_, shell, page) = OpenProtection();

        var open = page.OpenCaptivePortalCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.HasDialog.ShouldBeFalse("VPN выключен — пауза не нужна");
        await open;

        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        shell.Toast.Text.ShouldBe("Защита выключена — страница входа в сеть откроется и без паузы");
    }

    [AvaloniaFact]
    public void Proxy_mode_disables_the_kill_switch_and_says_why()
    {
        var (window, _, page) = OpenProtection();

        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Dispatcher.UIThread.RunJobs();

        Find<ToggleButton>(window, "KillSwitchToggle").IsEnabled.ShouldBeFalse();
        page.KillSwitchNote.ShouldBe("В режиме «Только прокси» блок при обрыве недоступен.");
        page.CanStrictMode.ShouldBeFalse();
    }

    private (MainWindow Window, ShellViewModel Shell, ProtectionPageViewModel Page) OpenProtection()
    {
        var (window, shell) = Open();
        return (window, shell, Navigate(shell));
    }

    private static ProtectionPageViewModel Navigate(ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(Pages.Protection);
        Dispatcher.UIThread.RunJobs();
        return shell.CurrentPage.ShouldBeOfType<ProtectionPageViewModel>();
    }
}
