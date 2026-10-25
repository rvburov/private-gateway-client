using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

// Одна коллекция на сборку: словарь языка и окна — общие, тесты идут по одному.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Главный экран и островок. Снимки каждого состояния — для сверки с макетом
/// (docs/design/screens/01-main.png).
/// </summary>
public sealed class MainWindowTests : WindowHarness
{
    [AvaloniaFact]
    public void Starts_off_with_real_ip_visible_to_sites()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        Text(window, "StatusTitle").ShouldBe("IP открыт");
        main.WebIp.ShouldBe("198.51.100.7");
        main.RealIpState.ShouldBe("виден сайтам");
        main.ServerTitle.ShouldBe("NL · Нидерланды 01");
        main.ServerMeta.ShouldBe("VLESS · REALITY · порт 443");
        Text(window, "StateLabel").ShouldBe("Выключено");
        Snapshot(window, "main-off");
    }

    [AvaloniaFact]
    public void Shield_connects_through_connecting_to_connected()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        Press(window, "Power");
        Text(window, "StatusTitle").ShouldBe("Подключение…");
        Text(window, "StateLabel").ShouldBe("Подключение");
        shell.Protection.IsBusy.ShouldBeTrue();
        shell.Protection.ToggleCommand.CanExecute(null).ShouldBeTrue("подключение можно прервать кнопкой");
        main.WebIp.ShouldBe("—");
        Snapshot(window, "main-connecting");

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);

        Text(window, "StatusTitle").ShouldBe("IP скрыт");
        Text(window, "StateLabel").ShouldBe("Защищено");
        main.StatusSubtitle.ShouldBe("Трафик зашифрован");
        main.WebIp.ShouldBe("203.0.113.24");
        main.WebIpMeta.ShouldBe("Амстердам · дата-центр · AS64500");
        main.RealIpState.ShouldBe("скрыт");
        main.Chips.Select(c => c.Value).ShouldBe(["через туннель", "запросы в туннеле", "нет обхода", "Kill Switch вкл"]);
        main.PingText.ShouldBe("42");

        Time.Advance(new TimeSpan(2, 14, 9));
        main.TimerText.ShouldBe("02:14:09");
        Snapshot(window, "main-connected");

        Press(window, "Power");
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
        Text(window, "StatusTitle").ShouldBe("IP открыт");
    }

    [AvaloniaFact]
    public async Task Copy_puts_exit_ip_into_clipboard_and_shows_toast()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        await main.CopyIpCommand.ExecuteAsync(null);

        Clipboard.Text.ShouldBe("203.0.113.24");
        shell.Toast.IsVisible.ShouldBeTrue();
        shell.Toast.Text.ShouldBe("IP скопирован");
        Snapshot(window, "main-toast");
    }

    [AvaloniaFact]
    public async Task Hidden_ip_is_masked_and_cannot_be_copied()
    {
        var (_, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        main.ToggleIpMaskCommand.Execute(null);

        main.WebIp.ShouldBe("•••.•.•••.••");
        main.CanCopyIp.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Choosing_another_server_while_protected_reconnects_to_it()
    {
        var (_, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        var quick = main.QuickServers;
        quick.Count.ShouldBe(3);
        quick[0].IsCurrent.ShouldBeTrue();
        quick[1].Name.ShouldBe("Финляндия 01");
        // Подключение к новому серверу ждет задержку имитации — часы в тесте двигаем сами.
        var choose = quick[1].ChooseCommand.ExecuteAsync("demo-fi-01");
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connecting);
        main.ServerTitle.ShouldBe("FI · Финляндия 01");

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await choose;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        shell.Protection.Status.ServerId.ShouldBe("demo-fi-01");
    }

    [AvaloniaFact]
    public void Chip_press_opens_its_explanation_and_closes_the_other()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        main.Chips[1].ToggleCommand.Execute(null);
        main.SelectedChip!.Id.ShouldBe("dns");
        main.Chips[3].ToggleCommand.Execute(null);

        main.Chips.Count(c => c.IsSelected).ShouldBe(1);
        main.SelectedChip!.Id.ShouldBe("ks");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "main-chip-info");
    }

    [AvaloniaFact]
    public void Island_navigates_between_pages_and_marks_active_item()
    {
        var (window, shell) = Open();

        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();

        shell.IsServers.ShouldBeTrue();
        shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        Buttons(window).Single(b => b.Classes.Contains("active")).CommandParameter.ShouldBe("servers");

        shell.NavigateCommand.Execute(Pages.Home);
        shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
    }

    [AvaloniaFact]
    public void Language_switch_updates_labels_at_once()
    {
        var (window, _) = Open();

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();

        Text(window, "StatusTitle").ShouldBe("IP exposed");
        Text(window, "StateLabel").ShouldBe("Off");
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("Settings");
        texts.ShouldContain("SITES SEE YOUR IP");
        texts.ShouldContain("demo");
        texts.ShouldNotContain("Настройки");
        Snapshot(window, "main-off-en");
    }

    [AvaloniaFact]
    public async Task Captive_pause_shows_the_countdown_and_resumes_by_the_banner_button()
    {
        var (window, shell) = Open();
        await Connect(shell);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        (await Service<ConnectionController>().PauseForCaptivePortalAsync()).ShouldBeTrue();
        WaitFor(() => shell.Protection.Status.IsPaused);

        Find<Border>(window, "PauseBanner").IsVisible.ShouldBeTrue();
        main.PauseLeft.ShouldBe("5:00");
        Text(window, "StatusTitle").ShouldBe("Защита на паузе");
        main.StatusSubtitle.ShouldBe("Вход в Wi-Fi отеля или кафе");
        main.HasTimer.ShouldBeFalse();
        main.CheckedText.ShouldBe("Проверки возобновятся после паузы");
        main.HasCheckAction.ShouldBeFalse();
        main.Chips.Select(c => c.Value).ShouldBe(["напрямую", "у провайдера", "открыт", "на паузе"]);
        main.WebIp.ShouldBe("198.51.100.7", "в паузе сайты видят реальный IP");
        main.WebIpMeta.ShouldBe("Пауза для входа в Wi-Fi · ваш провайдер");
        main.RealIpState.ShouldBe("виден сайтам");
        shell.Protection.Tone.ShouldBe(Tone.Partial);
        shell.Protection.Label.ShouldBe("Пауза");
        shell.Protection.ActionText.ShouldBe("Вернуть защиту");

        Time.Advance(TimeSpan.FromSeconds(61));
        main.PauseLeft.ShouldBe("3:59");
        Snapshot(window, "main-paused");

        Press(window, "ResumeButton");
        WaitFor(() => !shell.Protection.Status.IsPaused);

        Find<Border>(window, "PauseBanner").IsVisible.ShouldBeFalse();
        shell.Protection.Status.State.ShouldBe(ConnectionState.Connected);
        main.Chips.Select(c => c.Value).ShouldBe(["через туннель", "запросы в туннеле", "нет обхода", "Kill Switch вкл"]);
    }

    [AvaloniaFact]
    public async Task Traffic_outside_vpn_is_shown_on_main_while_protected_and_opens_routing()
    {
        var (window, shell) = Open();
        var settings = Service<SettingsService>();
        settings.Update(s => s with
        {
            Routing = new RoutingSettings(
                RoutingMode.Split,
                [new BypassApp("/opt/game/game", "game")],
                [new DomainRule("example.com", RuleAction.Direct), new DomainRule("ads.example.net", RuleAction.Block)]),
        });
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.HasBypass.ShouldBeFalse("VPN выключен — полоса не нужна");

        await Connect(shell);
        Dispatcher.UIThread.RunJobs();
        Find<Button>(window, "BypassBanner").IsVisible.ShouldBeTrue();
        Text(window, "BypassText").ShouldBe("Внимание · программы и сайты из списков идут мимо VPN");
        Snapshot(window, "main-bypass");

        settings.Update(s => s with { Routing = s.Routing with { Rules = [new DomainRule("ads.example.net", RuleAction.Block)] } });
        Dispatcher.UIThread.RunJobs();
        Text(window, "BypassText").ShouldBe("Внимание · программы из списка идут мимо VPN", "блок — не обход");
        settings.Update(s => s with
        {
            Routing = s.Routing with { Apps = [new BypassApp("/opt/game/game", "game", IsEnabled: false)], Rules = [new DomainRule("example.com", RuleAction.Direct)] },
        });
        main.BypassText.ShouldBe("Внимание · сайты из списка идут мимо VPN", "выключенная программа идет через VPN");
        settings.Update(s => s with { Routing = s.Routing with { Mode = RoutingMode.AllTraffic } });
        main.HasBypass.ShouldBeFalse("в режиме «Весь трафик» мимо VPN ничего не идет");
        settings.Update(s => s with { Mode = ConnectionMode.Proxy, Routing = s.Routing with { Mode = RoutingMode.Split } });
        main.HasBypass.ShouldBeFalse("прокси и так покрывает не все — об этом говорит сам режим");
        settings.Update(s => s with { Mode = ConnectionMode.Vpn });

        main.HasBypass.ShouldBeTrue();
        (await Service<ConnectionController>().PauseForCaptivePortalAsync()).ShouldBeTrue();
        WaitFor(() => shell.Protection.Status.IsPaused);
        main.HasBypass.ShouldBeFalse("в паузе — своя полоса");
        await Service<ConnectionController>().ResumeAsync();
        WaitFor(() => !shell.Protection.Status.IsPaused);
        Dispatcher.UIThread.RunJobs();

        Press(window, "BypassBanner");
        shell.CurrentKey.ShouldBe(Pages.Routing);
    }

    [AvaloniaFact]
    public async Task Proxy_mode_is_amber_and_says_what_is_not_protected()
    {
        var (window, shell) = Open();
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        await Connect(shell);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        Text(window, "StatusTitle").ShouldBe("Прокси включен");
        main.StatusSubtitle.ShouldBe("Защищен не весь трафик");
        main.Chips.Select(c => c.Value).ShouldBe(["только прокси", "частично", "не контролируется", "недоступен"]);
        main.CheckedText.ShouldBe("Блок при обрыве в режиме прокси недоступен");
        main.HasCheckAction.ShouldBeTrue();
        main.WebIpMeta.ShouldEndWith("только для программ через прокси");
        (shell.Protection.Tone, shell.Protection.Label, shell.Protection.Mark).ShouldBe((Tone.Partial, "Прокси", "Icon.Mark.Alert"));
        main.HasBypass.ShouldBeFalse();
        Snapshot(window, "main-proxy");

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
        Text(window, "StatusTitle").ShouldNotBe("Прокси включен");
    }

    [AvaloniaFact]
    public async Task Shield_during_the_pause_brings_protection_back_instead_of_turning_it_off()
    {
        var (_, shell) = Open();
        await Connect(shell);
        await Service<ConnectionController>().PauseForCaptivePortalAsync();
        WaitFor(() => shell.Protection.Status.IsPaused);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => !shell.Protection.Status.IsPaused);

        shell.Protection.Status.State.ShouldBe(ConnectionState.Connected);
        shell.Protection.Tone.ShouldBe(Tone.Connected);
    }

    [AvaloniaFact]
    public async Task Pause_ends_by_itself_after_five_minutes()
    {
        var (window, shell) = Open();
        await Connect(shell);
        await Service<ConnectionController>().PauseForCaptivePortalAsync();
        WaitFor(() => shell.Protection.Status.IsPaused);

        Time.Advance(ConnectionController.CaptivePause);
        WaitFor(() => !shell.Protection.Status.IsPaused);

        Find<Border>(window, "PauseBanner").IsVisible.ShouldBeFalse();
    }
}
