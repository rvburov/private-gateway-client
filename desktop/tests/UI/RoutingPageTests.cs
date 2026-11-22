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
        var (window, shell, page) = OpenRouting();
        var split = Find<RadioButton>(window, "Split");

        // Просьба пользователя 2026-11-05: «Раздельный режим» — через вопрос, как «Только прокси»; до ответа — «Весь трафик».
        split.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        (dialog.Title, dialog.PrimaryText, dialog.SecondaryText).ShouldBe(("Перейти в раздельный режим?", "Оставить весь трафик", "Перейти"));
        dialog.IsSecondaryDanger.ShouldBeTrue();
        dialog.Text.ShouldContain("реальный IP");
        (page.IsSplit, split.IsChecked).ShouldBe((false, false), "до ответа режим прежний");

        shell.Overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Mode.ShouldBe(RoutingMode.AllTraffic, "отказ — остаться на «Весь трафик»");

        split.Command.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.Mode.ShouldBe(RoutingMode.Split);
        split.IsChecked.ShouldBe(true);
        Find<Border>(window, "SplitWarning").IsVisible.ShouldBeTrue();
        Find<StackPanel>(window, "AppsSection").IsVisible.ShouldBeTrue();
        Find<TextBlock>(window, "AppsEmpty").IsVisible.ShouldBeTrue();

        Find<RadioButton>(window, "AllTraffic").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Mode.ShouldBe(RoutingMode.AllTraffic);
        page.IsSplit.ShouldBeFalse();
        shell.Overlay.Dialog.ShouldBeNull("обратно на «Весь трафик» — без вопроса");
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

    /// <summary>Решение 739: «Только этот адрес» — только у обычного домена; это отдельное правило от сайта с поддоменами.</summary>
    [AvaloniaFact]
    public void Exact_address_is_offered_for_plain_domains_and_kept_apart()
    {
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with { Rules = [new DomainRule("example.com", RuleAction.Proxy)] });

        Press(window, "AddRuleButton");
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        foreach (var pattern in new[] { "*.local", "geosite:google", "203.0.113.9", "198.51.100.0/24", string.Empty })
        {
            sheet.Pattern = pattern;
            sheet.CanExact.ShouldBeFalse(pattern);
        }

        sheet.Pattern = "Example.com";
        sheet.CanExact.ShouldBeTrue();
        Dispatcher.UIThread.RunJobs();
        Find<CheckBox>(window, "RuleExact").IsVisible.ShouldBeTrue();
        Find<TextBlock>(window, "RulePriority").IsVisible.ShouldBeTrue();
        sheet.Exact = true;
        sheet.IsBlock = true;
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.Rules.ShouldBe(
            [new DomainRule("example.com", RuleAction.Proxy), new DomainRule("example.com", RuleAction.Block, Exact: true)],
            "сайт с поддоменами и только этот адрес — разные правила");
        page.Rules.Select(r => r.IsExact).ShouldBe([false, true]);

        Press(window, "AddRuleButton");
        var zone = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        zone.Pattern = "*.example.org";
        zone.Exact = true;
        zone.SaveCommand.Execute(null);
        Service<SettingsService>().Current.Routing.Rules[^1].ShouldBe(new DomainRule("*.example.org", RuleAction.Block), "у зоны флажок не сохраняется");
    }

    /// <summary>
    /// Решение 741: «Все адреса YouTube» — у домена из списка сервисов, у нового правила отмечено; правило — «geosite:youtube»,
    /// в строке — подпись. Снятый флажок — правило для самого домена.
    /// </summary>
    [AvaloniaFact]
    public void All_addresses_of_a_service_are_offered_for_its_domains()
    {
        var (window, shell, page) = OpenRouting();

        Press(window, "AddRuleButton");
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        foreach (var pattern in new[] { "example.com", "*.youtube.com", "geosite:youtube", "203.0.113.9", string.Empty })
        {
            sheet.Pattern = pattern;
            sheet.CanAllAddresses.ShouldBeFalse(pattern);
        }

        sheet.Pattern = "m.YouTube.com";
        Dispatcher.UIThread.RunJobs();
        (sheet.CanAllAddresses, sheet.AllAddresses).ShouldBe((true, true), "у нового правила — сразу");
        Find<CheckBox>(window, "RuleService").IsVisible.ShouldBeTrue();
        sheet.AllAddressesText.ShouldBe("Все адреса YouTube");
        sheet.CanExact.ShouldBeFalse("все адреса — не только этот");
        sheet.IsProxy = true;
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([new DomainRule("geosite:youtube", RuleAction.Proxy)]);
        page.Rules.Single().ServiceText.ShouldBe("все адреса YouTube");

        Press(window, "AddRuleButton");
        var single = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        single.Pattern = "t.me";
        single.AllAddressesText.ShouldBe("Все адреса Telegram");
        single.AllAddresses = false;
        single.CanExact.ShouldBeTrue();
        single.SaveCommand.Execute(null);
        Service<SettingsService>().Current.Routing.Rules[^1].ShouldBe(new DomainRule("t.me", RuleAction.Block));
        page.Rules[^1].HasService.ShouldBeFalse();
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

        page.ChooseSplitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeFalse();
        page.Rules.ShouldAllBe(r => !r.IsIdle);
    }

    /// <summary>
    /// Решение 740: «Остальные сайты: Блокировать» — только через диалог с последствиями, обратно — сразу; предупреждение,
    /// «Пропускается всегда», «Напрямую» разрешает сайт, новое правило — «Через VPN».
    /// </summary>
    [AvaloniaFact]
    public void Blocking_other_sites_asks_first_and_lists_what_stays_allowed()
    {
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with { Rules = [new DomainRule("example.com", RuleAction.Direct)] });
        var block = Find<RadioButton>(window, "OthersBlock");
        (Find<RadioButton>(window, "OthersOpen").IsChecked, block.IsChecked).ShouldBe((true, false), "по умолчанию остальное открывается");
        Find<Border>(window, "OthersWarning").IsVisible.ShouldBeFalse();
        Find<StackPanel>(window, "AllowedSection").IsVisible.ShouldBeFalse();
        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeTrue();

        block.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        (dialog.Title, dialog.PrimaryText, dialog.SecondaryText).ShouldBe(("Блокировать все, кроме разрешенного?", "Отмена", "Блокировать остальное"));
        dialog.IsSecondaryDanger.ShouldBeTrue();
        dialog.Text.ShouldContain("мессенджеры");
        dialog.Text.ShouldContain("обновления системы");
        shell.Overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.BlockOthers.ShouldBeFalse("отказ — остальное открывается");
        block.IsChecked.ShouldBe(false);

        block.Command.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Routing.BlockOthers.ShouldBeTrue();
        block.IsChecked.ShouldBe(true);
        Find<Border>(window, "OthersWarning").IsVisible.ShouldBeTrue();
        Find<StackPanel>(window, "AllowedSection").IsVisible.ShouldBeTrue();
        Text(window, "AllowedUpdates").ShouldBe("Обновления системы и защиты");
        Text(window, "OthersNote").ShouldNotBeNull().ShouldContain("Открываются только сайты");
        Find<TextBlock>(window, "IdleNote").IsVisible.ShouldBeFalse("«Напрямую» разрешает сайт");
        page.Rules.Single().IsIdle.ShouldBeFalse();
        Find<Border>(window, "OthersWarning").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "routing-block-others");
        page.AddRuleCommand.Execute(null);
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().IsProxy.ShouldBeTrue("новое правило — разрешение");
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().CancelCommand.Execute(null);

        Find<RadioButton>(window, "OthersOpen").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.BlockOthers.ShouldBeFalse();
        shell.Overlay.Dialog.ShouldBeNull("обратно на «Открывать» — без вопроса");
        Find<StackPanel>(window, "AllowedSection").IsVisible.ShouldBeFalse();
    }

    /// <summary>
    /// Решение 742: PIN-код спрашивается перед тем, что ослабляет блок, — убрать правило «Блокировать», открыть остальные
    /// сайты, разрешить сайт при блоке остальных; добавить блок можно без кода. Неверный код не снимает блок.
    /// </summary>
    [AvaloniaFact]
    public void Pin_is_asked_before_the_block_gets_weaker()
    {
        var casino = new DomainRule("casino.example", RuleAction.Block);
        var (window, shell, page) = OpenRouting(RoutingSettings.Default with { Rules = [casino], BlockOthers = true });
        Text(window, "PinState").ShouldBe("Не задан: блок снимается без кода");
        Text(window, "BlockScope").ShouldBe("Правила действуют при подключенном VPN или явно включенном локальном фильтре");
        Service<SettingsService>().Update(s => s with { BlockPin = BlockPin.Create("1234") });
        Dispatcher.UIThread.RunJobs();
        Text(window, "PinState").ShouldBe("Задан: без него блок не снять");

        page.Rules.Single(r => r.Pattern == casino.Pattern).EditCommand.Execute(null);
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Title.ShouldBe("Снять блок с casino.example?");
        Find<Button>(window, "PinForgot").IsVisible.ShouldBeFalse("без аккаунта (демо) сбросить нечем");
        prompt.KeepCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([casino], "«Оставить блок»");

        page.Rules.Single(r => r.Pattern == casino.Pattern).EditCommand.Execute(null);
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Pin = "0000";
        prompt.ConfirmCommand.Execute(null);
        prompt.Error.ShouldBe("Неверный PIN-код");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "block-pin-prompt");
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([casino]);
        prompt.Pin = "1234";
        prompt.ConfirmCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBeEmpty("верный код снимает блок");
        shell.Overlay.HasSheet.ShouldBeFalse();

        // Блок без кода, разрешение при блоке остальных — с кодом.
        page.AddRuleCommand.Execute(null);
        var add = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        add.Pattern = "ads.example";
        add.IsBlock = true;
        add.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([new DomainRule("ads.example", RuleAction.Block)]);
        page.AddRuleCommand.Execute(null);
        add = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
        add.Pattern = "example.org";
        add.IsProxy = true;
        add.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>().Title.ShouldBe("Разрешить example.org?");
        shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>().KeepCommand.Execute(null);
        Service<SettingsService>().Current.Routing.Rules.Count.ShouldBe(1);

        Find<RadioButton>(window, "OthersOpen").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var others = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        others.Title.ShouldBe("Открыть остальные сайты?");
        others.Pin = "1234";
        others.ConfirmCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.Routing.BlockOthers.ShouldBeFalse();
    }

    /// <summary>Решение 742: пять неверных кодов — минута без попыток, и верный код в паузе не принимается.</summary>
    [AvaloniaFact]
    public void Five_wrong_pins_pause_the_attempts()
    {
        var casino = new DomainRule("casino.example", RuleAction.Block);
        var (_, shell, page) = OpenRouting(RoutingSettings.Default with { Rules = [casino] });
        Service<SettingsService>().Update(s => s with { BlockPin = BlockPin.Create("1234") });

        page.Rules.Single().EditCommand.Execute(null);
        shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>().DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        for (var i = 1; i < BlockPinGate.Attempts; i++)
        {
            prompt.Pin = "9999";
            prompt.ConfirmCommand.Execute(null);
            prompt.Error.ShouldBe("Неверный PIN-код");
        }

        prompt.Pin = "9999";
        prompt.ConfirmCommand.Execute(null);
        prompt.Error.ShouldBe("Слишком много попыток — подождите минуту");
        prompt.Pin = "1234";
        prompt.ConfirmCommand.Execute(null);
        prompt.Error.ShouldBe("Слишком много попыток — подождите минуту");
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([casino]);
    }

    /// <summary>Решение 742: задать, сменить (сначала текущий код) и убрать PIN-код; сброс — входом в аккаунт.</summary>
    [AvaloniaFact]
    public void Pin_is_set_changed_and_reset_through_the_account()
    {
        var (window, shell, page) = OpenRouting(account: true);

        page.ChangePinCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var setup = shell.Overlay.Sheet.ShouldBeOfType<BlockPinSetupViewModel>();
        Find<Button>(window, "RemovePin").IsVisible.ShouldBeFalse("убирать еще нечего");
        setup.Pin = "12";
        setup.SaveCommand.Execute(null);
        setup.Error.ShouldBe("Нужно от 4 до 8 цифр");
        setup.Pin = "123456";
        setup.Repeat = "123457";
        setup.SaveCommand.Execute(null);
        setup.Error.ShouldBe("Коды не совпадают");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "block-pin-setup");
        setup.Repeat = "123456";
        setup.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.BlockPin.ShouldNotBeNull().Matches("123456").ShouldBeTrue();
        Service<SettingsService>().Current.BlockPin!.Hash.ShouldNotContain("123456");
        Find<Button>(window, "ChangePin").Content.ShouldBe("Изменить");

        page.ChangePinCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Title.ShouldBe("Изменить PIN-код?");
        Find<Button>(window, "PinForgot").IsVisible.ShouldBeTrue("сброс — входом в аккаунт");
        prompt.ForgotCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var reset = shell.Overlay.Sheet.ShouldBeOfType<BlockPinResetViewModel>();
        reset.Email.ShouldBe(FakeAccountApi.Email, "почта аккаунта программы");
        reset.Password = "pw";
        reset.Email = "other@example.com";
        reset.ResetCommand.Execute(null);
        reset.Error.ShouldNotBeNull("другой аккаунт код не сбрасывает");
        AccountApi.Logins.ShouldBeEmpty();
        reset.Email = FakeAccountApi.Email;
        AccountApi.RequiresCode = true;
        reset.ResetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        reset.IsCodeStep.ShouldBeTrue("администратору — код 2FA");
        Service<SettingsService>().Current.BlockPin.ShouldNotBeNull();
        reset.Code = "123456";
        reset.ResetCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.BlockPin.ShouldBeNull("вошли — код снят, блоки остались");
        shell.Overlay.Sheet.ShouldBeOfType<BlockPinSetupViewModel>().Notice.ShouldBe("PIN-код сброшен. Блоки на месте — задайте новый код.");
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

    [AvaloniaFact]
    public void Adding_an_allow_cannot_replace_or_override_a_block_without_pin()
    {
        var blocked = new DomainRule("example.com", RuleAction.Block);
        var (_, shell, page) = OpenRouting(RoutingSettings.Default with { Rules = [blocked] });
        Service<SettingsService>().Update(settings => settings with { BlockPin = BlockPin.Create("1234") });
        Dispatcher.UIThread.RunJobs();
        foreach (var pattern in new[] { "example.com", "part.example.com" })
        {
            page.AddRuleCommand.Execute(null);
            var edit = shell.Overlay.Sheet.ShouldBeOfType<SiteRuleEditViewModel>();
            edit.Pattern = pattern;
            edit.IsProxy = true;
            edit.SaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
            prompt.KeepCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Service<SettingsService>().Current.Routing.Rules.ShouldBe([blocked]);
        }
    }

    private (MainWindow Window, ShellViewModel Shell, RoutingPageViewModel Page) OpenRouting(RoutingSettings? routing = null, bool account = false)
    {
        var (window, shell) = Open(account: account, signedIn: account);
        if (routing is not null)
        {
            Service<SettingsService>().Update(s => s with { Routing = routing });
        }

        shell.NavigateCommand.Execute(Pages.Routing);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<RoutingPageViewModel>());
    }
}
