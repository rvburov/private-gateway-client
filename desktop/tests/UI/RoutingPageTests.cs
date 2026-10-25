using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«Маршрутизация» (доска 13); снимок — для сверки с docs/design/screens/13-routing.png.</summary>
public sealed class RoutingPageTests : WindowHarness
{
    [AvaloniaFact]
    public void By_default_all_traffic_goes_through_vpn()
    {
        var (window, shell, page) = OpenRouting();

        shell.IsSettings.ShouldBeTrue();
        (page.IsAllTraffic, page.IsSplit).ShouldBe((true, false));
        Text(window, "ModeNote").ShouldBe($"Весь трафик {AppInfo.OsName} идет через VPN. Исключение — только локальная сеть, если она разрешена.");
        Find<Border>(window, "SplitWarning").IsVisible.ShouldBeFalse();
        Find<StackPanel>(window, "AppsSection").IsVisible.ShouldBeFalse("программы мимо VPN — только в раздельном режиме");
        Find<TextBlock>(window, "RulesEmpty").IsVisible.ShouldBeTrue();
        Text(window, "LanText").ShouldBe("Сейчас запрещена · настраивается в «Защите и утечках»");
    }

    [AvaloniaFact]
    public void Split_mode_shows_the_warning_apps_and_rules_as_on_the_board()
    {
        var (window, _, page) = OpenRouting(new RoutingSettings(
            RoutingMode.Split,
            [
                new BypassApp(@"C:\Games\Studio\bin\game.exe", "Игра"),
                new BypassApp(@"C:\Program Files\Bank\Client\bank.exe", "Банк-клиент"),
                new BypassApp(@"C:\Users\user\AppData\meet.exe", "Видеозвонки", IsEnabled: false),
            ],
            [
                new DomainRule("example.com", RuleAction.Direct),
                new DomainRule("*.local", RuleAction.Direct),
                new DomainRule("ads.example.net", RuleAction.Block),
                new DomainRule("example.org", RuleAction.Proxy),
            ]));

        Text(window, "ModeNote").ShouldBe("VPN для всего, кроме выбранных программ и сайтов.");
        Find<Border>(window, "SplitWarning").IsVisible.ShouldBeTrue();
        page.Apps.Select(a => (a.Letter, a.ShortPath, a.IsEnabled)).ShouldBe(
        [
            ("И", @"C:\Games\…\game.exe", true),
            ("Б", @"C:\Program Files\…\bank.exe", true),
            ("В", @"C:\Users\…\meet.exe", false),
        ]);
        page.Apps.Select(a => (a.IsTeal, a.IsAmber)).ShouldBe([(false, false), (true, false), (false, true)]);
        page.Rules.Select(r => r.ActionText).ShouldBe(["Напрямую", "Напрямую", "Блокировать", "Через VPN"]);
        page.Rules.ShouldAllBe(r => !r.IsIdle);
        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeFalse();
        Snapshot(window, "routing");

        // Низ страницы: правила для сайтов и «Всегда напрямую».
        window.GetVisualDescendants().OfType<RoutingPage>().Single().GetVisualDescendants().OfType<ScrollViewer>().First().ScrollToEnd();
        Snapshot(window, "routing-end");
    }

    [AvaloniaFact]
    public void Mode_is_saved_at_once()
    {
        var (window, _, page) = OpenRouting();

        Find<RadioButton>(window, "Split").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.Mode.ShouldBe(RoutingMode.Split);
        Find<Border>(window, "SplitWarning").IsVisible.ShouldBeTrue();
        Find<StackPanel>(window, "AppsSection").IsVisible.ShouldBeTrue();
        Find<TextBlock>(window, "AppsEmpty").IsVisible.ShouldBeTrue();

        Find<RadioButton>(window, "AllTraffic").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Mode.ShouldBe(RoutingMode.AllTraffic);
        page.IsSplit.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Apps_are_picked_switched_and_removed()
    {
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with { Mode = RoutingMode.Split });

        await page.AddAppCommand.ExecuteAsync(null);
        page.Apps.ShouldBeEmpty("окно выбора закрыто — ничего не добавлено");

        OpenFiles.Program = ("/opt/game/bin/game", "game");
        await page.AddAppCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Apps.ShouldBe([new BypassApp("/opt/game/bin/game", "game")]);
        page.Apps.ShouldHaveSingleItem().ShortPath.ShouldBe("/opt/…/game");
        page.Apps[0].Letter.ShouldBe("G");

        page.Apps[0].IsEnabled = false;
        Service<SettingsService>().Current.Routing.Apps[0].IsEnabled.ShouldBeFalse();
        await page.AddAppCommand.ExecuteAsync(null);
        Service<SettingsService>().Current.Routing.Apps.ShouldBe([new BypassApp("/opt/game/bin/game", "game")], "второй строки нет, программа снова включена");
        shell.Toast.Text.ShouldBe("«game» уже в списке — включена");

        OpenFiles.Program = ("/usr/bin/meet", "meet");
        await page.AddAppCommand.ExecuteAsync(null);
        page.Apps.Select(a => a.Name).ShouldBe(["game", "meet"]);
        page.Apps[0].RemoveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.Apps.ShouldBe([new BypassApp("/usr/bin/meet", "meet")]);
        page.Apps.ShouldHaveSingleItem().IsTeal.ShouldBeFalse("цвет плитки — по месту в списке");
        Find<TextBlock>(window, "AppsEmpty").IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void New_rule_is_checked_and_saved_from_the_sheet()
    {
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with { Mode = RoutingMode.Split });

        Press(window, "AddRuleButton");
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        (sheet.Title, sheet.SaveText, sheet.IsDirect).ShouldBe(("Новое правило", "Добавить", true), "в раздельном режиме правило заводят ради «Напрямую»");
        sheet.Pattern = "localhost";
        sheet.SaveCommand.Execute(null);
        sheet.Error.ShouldBe("Нужен домен, *.домен, geosite:категория, адрес или подсеть");
        shell.Overlay.HasSheet.ShouldBeTrue();

        sheet.Pattern = " https://Example.COM/login ";
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([new DomainRule("example.com", RuleAction.Direct)]);
        page.Rules.ShouldHaveSingleItem().Pattern.ShouldBe("example.com");
        Find<TextBlock>(window, "RulesEmpty").IsVisible.ShouldBeFalse();

        Press(window, "AddRuleButton");
        var subnet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        subnet.Pattern = "198.51.100.7/24";
        subnet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.Rules.Select(r => r.Pattern).ShouldBe(["example.com", "198.51.100.0/24"],
            "подсеть — правилом по адресу, к адресу сети");
    }

    [AvaloniaFact]
    public void Rule_is_edited_replaced_for_the_same_site_and_deleted()
    {
        var (_, shell, page) = OpenRouting(RoutingSettings.Default with
        {
            Mode = RoutingMode.Split,
            Rules = [new DomainRule("example.com", RuleAction.Direct), new DomainRule("example.org", RuleAction.Proxy)],
        });

        page.Rules[0].EditCommand.Execute(null);
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        (sheet.Title, sheet.Pattern, sheet.IsDirect, sheet.IsNew).ShouldBe(("Правило для сайта", "example.com", true, false));
        sheet.IsBlock = true;
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe(
            [new DomainRule("example.com", RuleAction.Block), new DomainRule("example.org", RuleAction.Proxy)], "правило — на своем месте");

        page.AddRuleCommand.Execute(null);
        sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        sheet.Pattern = "EXAMPLE.COM";
        sheet.IsProxy = true;
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe(
            [new DomainRule("example.org", RuleAction.Proxy), new DomainRule("example.com", RuleAction.Proxy)], "два правила на один сайт противоречили бы друг другу");

        page.Rules[0].EditCommand.Execute(null);
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([new DomainRule("example.com", RuleAction.Proxy)]);
    }

    [AvaloniaFact]
    public void Direct_rules_are_idle_outside_split_mode()
    {
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with
        {
            Rules = [new DomainRule("example.com", RuleAction.Direct), new DomainRule("ads.example.net", RuleAction.Block)],
        });

        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeTrue();
        page.Rules.Select(r => r.IsIdle).ShouldBe([true, false]);

        page.AddRuleCommand.Execute(null);
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        sheet.IsBlock.ShouldBeTrue("«Напрямую» в этом режиме не действует — новое правило блокирует");
        sheet.IsIdle.ShouldBeFalse();
        sheet.IsDirect = true;
        sheet.IsIdle.ShouldBeTrue();
        sheet.CancelCommand.Execute(null);

        page.IsSplit = true;
        Dispatcher.UIThread.RunJobs();
        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeFalse();
        page.Rules.ShouldAllBe(r => !r.IsIdle);
    }

    [AvaloniaFact]
    public void Local_network_is_described_and_opens_protection()
    {
        var (window, shell, _) = OpenRouting();
        Service<SettingsService>().Update(s => s with { Protection = s.Protection with { AllowLan = true } });
        Dispatcher.UIThread.RunJobs();

        Text(window, "LanText").ShouldBe("Сейчас разрешена · настраивается в «Защите и утечках»");
        Press(window, "LanLink");
        Dispatcher.UIThread.RunJobs();

        shell.CurrentKey.ShouldBe(Pages.Protection);
    }

    private (MainWindow Window, ShellViewModel Shell, RoutingPageViewModel Page) OpenRouting(RoutingSettings? routing = null)
    {
        var (window, shell) = Open();
        if (routing is not null)
        {
            Service<SettingsService>().Update(s => s with { Routing = routing });
        }

        shell.NavigateCommand.Execute(Pages.Routing);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<RoutingPageViewModel>());
    }
}
