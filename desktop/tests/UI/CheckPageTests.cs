using Avalonia;
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
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Экран «Проверка» на имитации службы; снимки — для сверки с docs/design/screens/07-diagnostics.png.</summary>
public sealed class CheckPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Checks_run_on_open_and_match_mockup_when_protected()
    {
        var (window, shell) = Open();
        await Connect(shell);

        var page = OpenCheck(shell);
        page.IsRunning.ShouldBeTrue("экран проверяет сразу");
        Text(window, "RunText").ShouldBe("Проверяем…");
        page.Rows.ShouldAllBe(r => r.State == CheckState.Waiting && r.ChipText == "…" && r.Detail == "Проверяется");
        Find<Button>(window, "RunButton").GetVisualDescendants().OfType<Icon>().Single().Classes.ShouldContain("spin");
        Snapshot(window, "check-running");

        Finish(page);

        Text(window, "RunText").ShouldBe("Проверить");
        Row(page, DiagnosticCheck.Service).ShouldBe(("Служба", "Имитация: службы нет, подключения ненастоящие", "Демо"));
        Row(page, DiagnosticCheck.Core).ShouldBe(("Сетевое ядро", "Xray-core запущен, конфигурация применена", "OK"));
        Row(page, DiagnosticCheck.AllTraffic).ShouldBe(("Весь трафик", "TUN-адаптер активен, маршрут по умолчанию → туннель", "OK"));
        Row(page, DiagnosticCheck.KillSwitch).Detail.ShouldStartWith("Правила брандмауэра активны: ");
        Row(page, DiagnosticCheck.Dns).ShouldBe(("DNS", "Запросов к DNS провайдера нет", "Нет утечек"));
        Row(page, DiagnosticCheck.Ipv6).ShouldBe(("IPv6", "Заблокирован вне туннеля", "Закрыт"));
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов", "203.0.113.24 · Амстердам · дата-центр", "Скрыт"));
        Row(page, DiagnosticCheck.OtherVpn).ShouldBe(("Другие VPN и прокси", "Не найдены", "Нет"));
        page.Journal.Select(j => j.Message).ShouldContain("tunnel up  server «Нидерланды 01»");
        page.Journal[0].Message.ShouldStartWith("checks done", Case.Sensitive, "новые записи сверху");
        Snapshot(window, "check");
    }

    [AvaloniaFact]
    public async Task Turning_protection_off_rechecks_and_shows_the_real_ip()
    {
        var (_, shell) = Open();
        await Connect(shell);
        var page = OpenCheck(shell);
        Finish(page);

        shell.Protection.ToggleCommand.Execute(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
        WaitFor(() => page.IsRunning);
        Finish(page);

        Row(page, DiagnosticCheck.AllTraffic).ShouldBe(("Весь трафик", "Идет напрямую через провайдера", "Напрямую"));
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов", "198.51.100.7 — ваш реальный IP", "Виден"));
        Row(page, DiagnosticCheck.Core).Chip.ShouldBe("Выкл.");
    }

    [AvaloniaFact]
    public async Task Block_shows_stopped_traffic_and_a_silent_server()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Servers);
        var servers = (ServersPageViewModel)shell.CurrentPage!;
        var tr = servers.Groups[0].Rows.Single(r => r.Name == "Турция 01");
        var choose = tr.ChooseCommand.ExecuteAsync(tr.Id);
        await choose;
        var connect = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Blocked);

        var page = OpenCheck(shell);
        Finish(page);

        Row(page, DiagnosticCheck.AllTraffic).ShouldBe(("Весь трафик", "Остановлен блоком при обрыве — утечки нет", "Стоп"));
        Row(page, DiagnosticCheck.Core).ShouldBe(("Сетевое ядро", "Запущено, но сервер не отвечает", "Нет ответа"));
        Row(page, DiagnosticCheck.ExitIp).Chip.ShouldBe("Скрыт");
        page.Journal.Select(j => j.Level).ShouldContain("WRN");
        Snapshot(window, "check-blocked");

        // Ошибки и предупреждения — отдельным разделом отчета, до обычного журнала: при подробном журнале ядра последние строки
        // заняты его событиями, и ошибка первой попытки подключения в отчет не попадала (у пользователя 2026-10-31).
        var report = page.BuildReport().Split(Environment.NewLine);
        var problems = Array.IndexOf(report, "Ошибки и предупреждения (весь журнал окна)");
        problems.ShouldBeGreaterThan(0);
        report[problems + 1].ShouldContain(" WRN ");
        problems.ShouldBeLessThan(Array.IndexOf(report, "Журнал (секреты скрыты)"));
    }

    [AvaloniaFact]
    public void Things_on_this_computer_that_may_interfere_are_named_and_a_dead_user_proxy_is_fixed()
    {
        // У пользователя на Windows 2026-10-31 консольные программы ломались из-за оставшегося https_proxy на вход Hiddify.
        Machine.Found.AddRange(
        [
            new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://user:secret@127.0.0.1:2334", HindranceScope.Machine),
            new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://127.0.0.1:2334", HindranceScope.User),
            new Hindrance(HindranceKind.WinHttpProxy, "WinHttpSettings", "198.51.100.5:3128", HindranceScope.Machine),
            new Hindrance(HindranceKind.AutoDetect, "AutoDetect", string.Empty, HindranceScope.User),
        ]);
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Entries = new InterferenceProbe(
                [new EntryCheck("127.0.0.1:2334", false, null), new EntryCheck("198.51.100.5:3128", true, null)],
                [new OtherClient("Hiddify", OtherClientSign.Process, "HiddifyCli")]),
        });
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning && page.Hindrances.Count == 5);

        page.Hindrances.Select(h => (h.Title, h.ChipText)).ShouldBe(
        [
            ("Переменная https_proxy (вся машина)", "Не отвечает"),
            ("Переменная https_proxy (пользователь)", "Не отвечает"),
            ("Прокси WinHTTP (вся машина)", "Работает"),
            ("Автоопределение прокси (WPAD)", "Включено"),
            ("Hiddify", "Работает"),
        ]);
        page.Hindrances[0].Detail.ShouldStartWith("http://***@127.0.0.1:2334 — не отвечает", Case.Sensitive, "имя и пароль прокси скрыты");
        page.Hindrances[0].Detail.ShouldContain("нужны права администратора");
        page.Hindrances[0].CanFix.ShouldBeFalse("переменную машины окно не трогает — нужны права администратора");
        page.Hindrances[2].Detail.ShouldContain("Закройте программу");
        page.Hindrances.Select(h => h.CanFix).ShouldBe([false, true, false, false, false], "исправить — только мертвый прокси пользователя");
        var report = page.BuildReport();
        report.ShouldContain("Что на этом компьютере может мешать");
        report.ShouldContain("[Не отвечает] Переменная https_proxy (пользователь) — http://127.0.0.1:2334 — не отвечает");
        report.ShouldNotContain("secret");
        Find<ItemsControl>(window, "Hindrances").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "check-hindrances");

        page.Hindrances[1].FixCommand.Execute(null);
        WaitFor(() => page.Hindrances.Count == 4);

        Machine.Fixed.ShouldBe([new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://127.0.0.1:2334", HindranceScope.User)]);
        shell.Toast.Text.ShouldStartWith("Исправлено. Прежнее значение — в файле /backups/settings.json.");

        Machine.Found.Clear();
        service!.Entries = new InterferenceProbe([], []);
        page.RunCommand.Execute(null);
        WaitFor(() => !page.IsRunning && page.Hindrances.Count == 1);
        (page.Hindrances[0].Title, page.Hindrances[0].ChipText, page.Hindrances[0].State).ShouldBe(("Ничего не найдено", "Нет", CheckState.Ok));
    }

    [AvaloniaFact]
    public async Task Ping_tile_opens_the_delay_block_with_three_numbers_and_where_the_new_connection_time_comes_from()
    {
        // У пользователя 2026-10-31 пинг 480 мс против 124 у других клиентов: блок показывает, из чего складывается число.
        var (window, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Delay = new DelayBreakdown(120, 124, 480, Time.GetUtcNow()),
        });
        Service<IServerStore>().Add([LinkParser.Parse("vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?encryption=none&security=reality"
            + "&sni=www.example.com&fp=chrome&pbk=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb&sid=0123abcd&type=tcp#R").Server!], null);
        var before = OpenCheck(shell);
        WaitFor(() => !before.IsRunning);
        before.DelayHint.ShouldBe("Подключитесь — задержка меряется через сервер");
        ScrollOf(window).Offset.Y.ShouldBe(0, "без плитки экран открывается сверху");

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        shell.NavigateCommand.Execute(Pages.Home);
        Dispatcher.UIThread.RunJobs();
        Press(window, "PingTile");
        Dispatcher.UIThread.RunJobs();

        var page = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !page.IsRunning && !page.IsMeasuringDelay && page.Delay is not null);
        WaitFor(() => ScrollOf(window).Offset.Y > 0);
        var header = Find<Grid>(window, "DelayHeader");
        header.TranslatePoint(new Avalonia.Point(0, 0), ScrollOf(window))!.Value.Y.ShouldBeInRange(0, ScrollOf(window).Viewport.Height,
            "плитка «Пинг» открывает «Проверку» на блоке «Задержка»");
        page.DelayRows.Select(r => (r.Title, r.Value)).ShouldBe([("До сервера", "120 мс"), ("Через сервер", "124 мс"), ("Новое соединение", "480 мс")]);
        page.DelayWhy.ShouldBe("Откуда 480 мс: на каждое новое соединение уходит примерно 4 круга до сервера (соединение с сервером → рукопожатие "
            + "REALITY → шифрование до сайта → запрос), а один круг у вас — около 120 мс.");
        page.DelayUpdated.ShouldStartWith("Обновлено ");
        page.HasDelayHint.ShouldBeFalse();
        var report = page.BuildReport();
        report.ShouldContain("Задержка");
        report.ShouldContain("  До сервера: 120 мс — прямое соединение с сервером — один круг по сети");
        report.ShouldContain("примерно 4 круга до сервера");
        Snapshot(window, "check-delay");
        page.TakeFocusDelay().ShouldBeFalse("просьба прокрутить — одна, ее уже забрал экран");
    }

    private static ScrollViewer ScrollOf(Avalonia.Controls.Window window) =>
        window.GetVisualDescendants().OfType<CheckPage>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();

    [AvaloniaFact]
    public void Without_the_service_the_settings_are_shown_unchecked_and_not_fixable()
    {
        Machine.Found.Add(new Hindrance(HindranceKind.EnvironmentProxy, "http_proxy", "127.0.0.1:2334", HindranceScope.User));
        var (_, shell) = Open();
        var page = OpenCheck(shell);
        Finish(page);
        WaitFor(() => page.Hindrances.Count == 1);

        (page.Hindrances[0].ChipText, page.Hindrances[0].CanFix).ShouldBe(("Не проверен", false), "без ответа службы прокси может быть живым");
        page.Hindrances[0].Detail.ShouldStartWith("127.0.0.1:2334 — не проверен: служба не отвечает.");
    }

    [AvaloniaFact]
    public async Task Exit_ip_in_proxy_mode_is_not_called_hidden()
    {
        // У пользователя на Windows (2026-10-30) браузер шел мимо прокси, а проверка писала «Скрыт»: IP сервера видят только
        // сайты, которые открывают программы через прокси ядра.
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Checks = [new DiagnosticResult(DiagnosticCheck.ExitIp, CheckState.Ok)],
        });
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status is { State: ConnectionState.Connected, Mode: ConnectionMode.Proxy });
        service!.Push(s => s with { Exit = ExitInfo.Unknown with { Ip = "203.0.113.24" } });
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning);

        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов",
            "203.0.113.24 — только для программ, которые идут через прокси; остальные — напрямую, с вашим IP", "Через прокси"));
    }

    [AvaloniaFact]
    public async Task Exit_ip_shows_when_the_service_measures_it_and_a_leak_or_silence_is_named()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Checks = [new DiagnosticResult(DiagnosticCheck.ExitIp, CheckState.Ok)],
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        var page = OpenCheck(shell);
        WaitFor(() => !page.IsRunning);
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов", "Работает", "Скрыт"), "IP еще не пришел — пустой строки нет");

        // Замер служба присылает в состоянии — строка обновляется сразу, без новой проверки.
        service!.Push(s => s with { Exit = ExitInfo.Unknown with { Ip = "203.0.113.24", DirectIp = "198.51.100.7" } });
        WaitFor(() => Row(page, DiagnosticCheck.ExitIp).Detail == "203.0.113.24");

        service.Checks = [new DiagnosticResult(DiagnosticCheck.ExitIp, CheckState.Warning, "same_ip")];
        page.RunCommand.Execute(null);
        WaitFor(() => !page.IsRunning);
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов", "Сайты видят ваш реальный IP — трафик идет мимо туннеля", "Виден"));
        Snapshot(window, "check-leak");

        service.Checks = [new DiagnosticResult(DiagnosticCheck.ExitIp, CheckState.Warning, "no_answer")];
        page.RunCommand.Execute(null);
        WaitFor(() => !page.IsRunning);
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP для сайтов", "Не измерен: проверочный адрес не ответил", "Нет ответа"));

        // Замер по IPv6: сайты видят адрес IPv6 мимо туннеля, хотя настройки его закрывают.
        service.Checks = [new DiagnosticResult(DiagnosticCheck.Ipv6, CheckState.Warning, "leak")];
        page.RunCommand.Execute(null);
        WaitFor(() => !page.IsRunning);
        Row(page, DiagnosticCheck.Ipv6).ShouldBe(("IPv6", "Сайты видят ваш адрес IPv6: замер показал, что IPv6 идет мимо туннеля", "Виден"));

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Row(page, DiagnosticCheck.Ipv6).ShouldBe(("IPv6", "Sites see your IPv6 address: the measurement shows IPv6 going around the tunnel", "Visible"));
    }

    [AvaloniaFact]
    public void Export_saves_an_anonymous_report()
    {
        var (_, shell) = Open();
        var page = OpenCheck(shell);
        Finish(page);

        page.ExportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Files.Name.ShouldBe("private-gateway-report-20261024-1542.txt");
        var report = Files.Text.ShouldNotBeNull();
        report.ShouldContain("Отчет проверки Private Gateway Client 0.1");
        report.ShouldContain("Состояние: Выключено");
        report.ShouldContain("[Виден] IP для сайтов — ••• — ваш реальный IP");
        report.ShouldNotContain("198.51.100.7", customMessage: "реальный IP пользователя в отчет не попадает");
        report.ShouldNotContain("Home", customMessage: "имя Wi-Fi в отчет не попадает");
        report.ShouldContain("service started  ipc=ok");
        shell.Toast.Text.ShouldBe("Отчет сохранен");
    }

    [AvaloniaFact]
    public async Task Export_under_protection_adds_the_inspector_without_addresses_macs_or_sites()
    {
        var (_, shell) = Open();
        await Connect(shell);
        var page = OpenCheck(shell);
        Finish(page);

        page.ExportCommand.Execute(null);
        WaitFor(() => Files.Text is not null);

        var report = Files.Text!;
        report.ShouldContain("Сеть машины");
        report.ShouldContain("  Wi-Fi · Wi-Fi · Включен");
        report.ShouldContain("MAC: [MAC]");
        report.ShouldContain("Шлюз: [адрес-", customMessage: "адреса демо — из диапазонов документации, и для отчета они публичные");
        report.ShouldContain("До и после VPN");
        report.ShouldContain("[сменился] IP для сайтов: [адрес-");
        report.ShouldContain("соединений: 5 · ждут входящих: 3 · TCP: 6 · UDP: 2");
        report.ShouldContain("Отчет обезличен");
        foreach (var leak in new[] { "203.0.113.24", "198.51.100.7", "2001:db8", "00:00:5e:00:53", "203.0.113.80", "198.51.100.200", "firefox", "telegram" })
        {
            report.ShouldNotContain(leak, customMessage: "в отчете нет " + leak);
        }
    }

    [AvaloniaFact]
    public void Cancelled_export_says_nothing()
    {
        var (_, shell) = Open();
        var page = OpenCheck(shell);
        Finish(page);
        Files.Accept = false;

        page.ExportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Toast.IsVisible.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Link_pasted_as_a_server_name_is_masked_on_screen_and_in_report()
    {
        const string link = "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=reality#NL";
        var (_, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Servers);
        var servers = (ServersPageViewModel)shell.CurrentPage!;
        var nl = servers.Groups[0].Rows.Single(r => r.Name == "Нидерланды 01");
        nl.OpenCommand.Execute(nl.Id);
        var card = shell.Overlay.Sheet.ShouldBeOfType<ServerCardViewModel>();
        card.StartRenameCommand.Execute(null);
        card.NewName = link[..64];
        card.SaveNameCommand.Execute(null);
        shell.Overlay.CloseSheet();
        await Connect(shell);

        var page = OpenCheck(shell);
        Finish(page);
        page.ExportCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.Journal.ShouldContain(j => j.Message.StartsWith("connect  server «vless://", StringComparison.Ordinal));
        page.Journal.ShouldNotContain(j => j.Message.Contains("11111111-2222", StringComparison.Ordinal));
        Files.Text!.ShouldNotContain("11111111-2222");
    }

    [AvaloniaFact]
    public void English_labels_switch_at_once()
    {
        var (window, shell) = Open();
        var page = OpenCheck(shell);
        Finish(page);

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();

        Text(window, "RunText").ShouldBe("Run checks");
        Row(page, DiagnosticCheck.ExitIp).ShouldBe(("IP seen by sites", "198.51.100.7 — your real IP", "Visible"));
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("LOG");
        Snapshot(window, "check-en");
    }

    private static CheckPageViewModel OpenCheck(ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        return shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
    }

    private void Finish(CheckPageViewModel page)
    {
        Time.Advance(SimulatedGatewayClient.ChecksDelay);
        WaitFor(() => !page.IsRunning);
    }

    private static (string Title, string Detail, string Chip) Row(CheckPageViewModel page, DiagnosticCheck check)
    {
        var row = page.Rows.Single(r => r.Check == check);
        return (row.Title, row.Detail, row.ChipText);
    }
}
