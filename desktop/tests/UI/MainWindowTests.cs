using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    public void Strict_mode_shield_closes_the_network_and_opens_the_tunnel_without_asking()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        Service<SettingsService>().Update(s => s with { Protection = s.Protection with { StrictMode = true } });
        Press(window, "Power");
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);

        Press(window, "Power");
        WaitFor(() => shell.Protection.Status.StrictBlock);

        (Text(window, "StatusTitle"), main.StatusSubtitle, Text(window, "StateLabel"))
            .ShouldBe(("Интернет заблокирован", "Строгий режим: без VPN сеть закрыта", "Блокировка"));
        shell.Protection.ActionText.ShouldBe(Loc.Instance["power.action.connect"], "щит в строгом режиме включает защиту");
        (main.CheckedText, main.CheckActionText).ShouldBe(("Выключается в «Защите и утечках»", "Вход в Wi-Fi"), "подключает щит, а сеть отеля без входа не пустит");
        Snapshot(window, "main-strict");

        Press(window, "Power");
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);

        shell.Overlay.HasDialog.ShouldBeFalse("о снятии защиты спрашивать не о чем — она включается");
    }

    [AvaloniaFact]
    public async Task Strict_block_pauses_for_wifi_sign_in_and_closes_the_network_after()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        Service<SettingsService>().Update(s => s with { Protection = s.Protection with { StrictMode = true } });
        Press(window, "Power");
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        Press(window, "Power");
        WaitFor(() => shell.Protection.Status.StrictBlock);

        var open = main.CheckActionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        dialog.Text.ShouldBe(Loc.Instance["pause.dialog.textStrict"], "после паузы сеть закроется, а не вернется VPN");
        dialog.SecondaryCommand.Execute(null);
        await open;
        WaitFor(() => shell.Protection.Status.IsPaused);

        Urls.Opened.ShouldBe([CaptivePortal.CheckPage]);
        shell.Toast.Text.ShouldBe("Пауза на 5 минут: войдите в сеть на открывшейся странице");
        Find<Border>(window, "PauseBanner").IsVisible.ShouldBeTrue();
        Find<Button>(window, "ResumeButton").Content.ShouldBe("Закрыть сеть");
        (Text(window, "StatusTitle"), main.CheckedText).ShouldBe(("Защита на паузе", "После паузы сеть снова закроется"));
        (shell.Protection.Tone, shell.Protection.ActionText).ShouldBe((Tone.Partial, "Включить защиту"), "вошли в сеть — щит подключает VPN");
        Snapshot(window, "main-strict-paused");

        Press(window, "ResumeButton");
        WaitFor(() => !shell.Protection.Status.IsPaused);

        (shell.Protection.Status.State, shell.Protection.Status.StrictBlock).ShouldBe((ConnectionState.Blocked, true));
        (Text(window, "StatusTitle"), main.StatusSubtitle, shell.Protection.Tone).ShouldBe(("Интернет заблокирован", "Строгий режим: без VPN сеть закрыта", Tone.Blocked));

        (await Service<ConnectionController>().PauseForCaptivePortalAsync()).ShouldBeTrue();
        WaitFor(() => shell.Protection.Status.IsPaused);
        Time.Advance(ConnectionController.CaptivePause);
        WaitFor(() => !shell.Protection.Status.IsPaused);
        shell.Protection.Status.StrictBlock.ShouldBeTrue("пауза кончилась сама — сеть снова закрыта");

        (await Service<ConnectionController>().PauseForCaptivePortalAsync()).ShouldBeTrue();
        WaitFor(() => shell.Protection.Status.IsPaused);
        Press(window, "Power");
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);

        (shell.Overlay.HasDialog, shell.Protection.Status.IsPaused, shell.Protection.Status.StrictBlock).ShouldBe((false, false, false));
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
    public async Task Split_mode_shows_all_traffic_as_partial_on_main_and_in_checks()
    {
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            // Как служба в раздельном режиме с обходом: «Весь трафик» — частично.
            Outcome = c => c.MoveTo(ConnectionState.Connected, Time.GetUtcNow()) is var connected
                ? connected with { Protection = connected.Protection with { AllTraffic = CheckState.Warning } }
                : c,
            Checks = [new DiagnosticResult(DiagnosticCheck.AllTraffic, CheckState.Warning, "split")],
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();

        var chip = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>().Chips[0];
        (chip.Value, chip.Text).ShouldBe(("частично",
            "Программы и сайты из раздельного режима идут напрямую — мимо туннеля и блока при обрыве. Остальное — через туннель."),
            "в режиме VPN «частично» — раздельный режим, а не «только прокси»");

        shell.NavigateCommand.Execute(Pages.Check);
        var check = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !check.IsRunning);
        var row = check.Rows.Single(r => r.Check == DiagnosticCheck.AllTraffic);
        (row.Detail, row.ChipText).ShouldBe(("Программы и сайты из раздельного режима идут мимо туннеля", "Частично"));
    }

    [AvaloniaTheory]
    [InlineData(true, CheckState.Ok, "IPv6 в туннеле", "через сервер", "Сервер открывает IPv6: сайты по IPv6 идут через туннель, как и по IPv4.",
        "Идет через туннель: сервер открывает IPv6", "В туннеле")]
    [InlineData(false, CheckState.Warning, "IPv6 открыт", "мимо туннеля",
        "Сервер без IPv6, и IPv6 идет напрямую, мимо туннеля: сайты видят ваш адрес IPv6. Закроет его «Блокировать IPv6 вне туннеля» или блок при обрыве.",
        "Идет мимо туннеля: сайты видят ваш адрес IPv6", "Открыт")]
    [InlineData(false, CheckState.Ok, "IPv6 закрыт", "нет обхода",
        "IPv6 закрыт: сервер без IPv6 или выбрано «Только IPv4» — трафик IPv6 не уйдет мимо туннеля.", "Заблокирован вне туннеля", "Закрыт")]
    public async Task Ipv6_chip_tells_tunneled_open_and_closed_apart(
        bool tunnel, CheckState state, string title, string value, string text, string detail, string chipText)
    {
        var reason = tunnel ? "tunnel" : state == CheckState.Warning ? "outside" : null;
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            // Как служба в режиме VPN: сервер открывает IPv6, или IPv6 идет мимо туннеля, или закрыт.
            Outcome = c => c.MoveTo(ConnectionState.Connected, Time.GetUtcNow()) is var connected
                ? connected with { Protection = connected.Protection with { Ipv6 = state, Ipv6Tunnel = tunnel } }
                : c,
            Checks = [new DiagnosticResult(DiagnosticCheck.Ipv6, state, reason)],
        });
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Vpn });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();

        var chip = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>().Chips[2];
        (chip.Title, chip.Value, chip.Text).ShouldBe((title, value, text));

        shell.NavigateCommand.Execute(Pages.Check);
        var check = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !check.IsRunning);
        var row = check.Rows.Single(r => r.Check == DiagnosticCheck.Ipv6);
        (row.Detail, row.ChipText).ShouldBe((detail, chipText));
    }

    [AvaloniaTheory]
    [InlineData(false, "lan_dns", "DNS виден сети", "роутер видит", "Локальная сеть разрешена, а «Блокировать DNS провайдера» выключено",
        "Запросы к DNS роутера могут идти мимо туннеля: локальная сеть разрешена", "Видны роутеру")]
    [InlineData(true, "own_dns", "DNS в своей сети", "свой DNS", "DNS — свой адрес в локальной сети",
        "Запросы идут к своему DNS в локальной сети, мимо туннеля", "Свой DNS")]
    [InlineData(null, "ipv6_dns", "DNS по IPv6 открыт", "мимо туннеля", "IPv6 мимо туннеля открыт, а «Блокировать DNS провайдера» выключено",
        "Запросы по IPv6 могут идти к DNS провайдера мимо туннеля", "Видны провайдеру")]
    public async Task Dns_chip_tells_who_sees_the_lookups_outside_the_tunnel(
        bool? ownDns, string reason, string title, string value, string text, string detail, string chipText)
    {
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            // Как служба в режиме VPN: DNS мимо туннеля — к роутеру разрешенной локальной сети или к своему DNS в сети.
            Outcome = c => c.MoveTo(ConnectionState.Connected, Time.GetUtcNow()) is var connected
                ? connected with { Protection = connected.Protection with { Dns = CheckState.Warning } }
                : c,
            Checks = [new DiagnosticResult(DiagnosticCheck.Dns, CheckState.Warning, reason)],
        });
        Service<SettingsService>().Update(s => s with
        {
            Mode = ConnectionMode.Vpn,
            Protection = ownDns switch
            {
                true => s.Protection with { Dns = DnsChoice.Custom, CustomDns = "192.168.1.53" },
                false => s.Protection with { AllowLan = true, BlockOutsideDns = false },
                null => s.Protection with { BlockIpv6 = false, BlockOutsideDns = false },
            },
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();

        var chip = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>().Chips[1];
        (chip.Title, chip.Value).ShouldBe((title, value));
        chip.Text.ShouldStartWith(text);

        shell.NavigateCommand.Execute(Pages.Check);
        var check = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !check.IsRunning);
        var row = check.Rows.Single(r => r.Check == DiagnosticCheck.Dns);
        (row.Detail, row.ChipText).ShouldBe((detail, chipText));
    }

    [AvaloniaFact]
    public async Task Check_button_comes_back_even_if_the_service_sends_no_check_time()
    {
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        await main.CheckActionCommand.ExecuteAsync(null);

        main.IsChecking.ShouldBeFalse("проверка закончилась — «Проверяем…» не висит");
        main.HasCheckAction.ShouldBeTrue();
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

    [AvaloniaTheory]
    [InlineData(ConnectionMode.Vpn, false, "PGC-SVC-002 · трафик заблокирован")]
    [InlineData(ConnectionMode.Proxy, false, "PGC-SVC-002 · прокси остановлен")]
    [InlineData(ConnectionMode.Vpn, true, "PGC-SVC-002 · защиты нет, трафик идет напрямую")]
    public async Task Error_says_whether_traffic_is_blocked(ConnectionMode mode, bool unprotected, string subtitle)
    {
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Outcome = connecting => (connecting with { ErrorCode = ErrorCodes.CoreFailed }).MoveTo(ConnectionState.Error, Time.GetUtcNow()) is var error
                && unprotected
                ? error with { Protection = ProtectionChecks.For(ConnectionState.Off) }
                : error,
        });
        Service<SettingsService>().Update(s => s with { Mode = mode });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Error);

        shell.CurrentPage.ShouldBeOfType<MainPageViewModel>().StatusSubtitle.ShouldBe(subtitle);
        shell.CurrentKey.ShouldBe(Pages.Home, "ошибка ядра — не экран ошибки службы");
    }

    [AvaloniaTheory]
    [InlineData(true, ErrorCodes.ServerNotFound, "PGC-NET-002 · строгий режим: без VPN сеть закрыта", Pages.Home)]
    [InlineData(false, ErrorCodes.ServerNotFound, "PGC-NET-002 · под блоком адрес не найти: отключите защиту", Pages.Home)]
    [InlineData(false, ErrorCodes.UnsupportedServer, "PGC-SVC-005 · трафик заблокирован", Pages.Home)]
    [InlineData(true, ErrorCodes.OtherVpn, "PGC-NET-010 · строгий режим: без VPN сеть закрыта", Pages.Problem)]
    public async Task Refusal_that_keeps_the_block_is_shown_with_its_reason(bool strict, string code, string subtitle, string page)
    {
        // Строгий режим, загрузка и блок при обрыве: отказ в подключении сеть не открывает — «Блокировка» с причиной.
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Outcome = connecting => connecting.MoveTo(ConnectionState.Blocked, Time.GetUtcNow()) with { StrictBlock = strict, ErrorCode = code },
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.ErrorCode == code);

        main.StatusSubtitle.ShouldBe(subtitle);
        WaitFor(() => shell.CurrentKey == page);
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
