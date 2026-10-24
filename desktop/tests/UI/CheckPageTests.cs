using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Core;
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
