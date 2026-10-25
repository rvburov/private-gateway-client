using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
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
