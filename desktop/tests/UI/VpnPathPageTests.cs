using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Как меняется путь при VPN» — сценарий лаборатории (этап 8): карта без защиты и под ней и строки «было → стало»; при
/// выключенной защите вторая карта — модель, ее туннель и строки — «условно». Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class VpnPathPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Off_shows_a_model_and_connecting_turns_it_into_measurements()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "VpnPathButton");
        var page = shell.CurrentPage.ShouldBeOfType<VpnPathPageViewModel>();
        WaitFor(() => Nodes(page.Before).Any(n => n.Title == "Wi-Fi"));
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");

        page.IsModel.ShouldBeTrue();
        page.AfterNote.ShouldStartWith("Модель: защита выключена");
        Nodes(page.Before).Select(n => n.Title).ShouldBe(["Этот компьютер", "Wi-Fi", "Роутер", "Провайдер", "Сайт"]);
        Nodes(page.After).Select(n => n.Title).ShouldBe(["Этот компьютер", "Wi-Fi", "Роутер", "Провайдер", "VPN-сервер", "Сайт"]);
        Links(page.After).Where(l => l.IsTunnel).ShouldAllBe(l => !l.IsMeasured, "туннель модели не измерен");
        Nodes(page.After)[4].IsMeasured.ShouldBeFalse();
        page.Changes.Select(c => (c.Label, c.Before, c.After, c.IsMeasured)).ShouldBe([
            ("Сайты видят", "198.51.100.7", "адрес сервера", false),
            ("DNS", "192.0.2.1", "172.19.0.2 — в туннеле", false),
            ("Маршрут в интернет", "Wi-Fi через 192.0.2.1", "туннель: 0.0.0.0/1, 128.0.0.0/1", false),
            ("Провайдер видит", "адреса сайтов и их имена (DNS, SNI)", "только зашифрованный поток к серверу", false),
        ]);

        await Connect(shell);
        await page.RefreshCommand.ExecuteAsync(null);

        page.IsModel.ShouldBeFalse();
        page.HasAfterNote.ShouldBeFalse("настоящий туннель — без оговорок");
        Nodes(page.After)[4].IsMeasured.ShouldBeTrue("адрес выхода измерен");
        Links(page.After)[3].IsMeasured.ShouldBeTrue("участок до сервера — по замеру");
        page.Changes.Select(c => (c.Label, c.Before, c.After, c.IsMeasured)).ShouldBe([
            ("Сайты видят", "198.51.100.7", "203.0.113.24", true),
            ("DNS", "192.0.2.1", "172.19.0.2", true),
            ("Маршрут в интернет", "Wi-Fi через 192.0.2.1", "туннель: 0.0.0.0/1, 128.0.0.0/1", true),
            ("Провайдер видит", "адреса сайтов и их имена (DNS, SNI)", "только зашифрованный поток к серверу", false),
        ], "что видит провайдер — объяснение, а не замер");
        page.Changes[0].EvidenceText.ShouldBe("измерено");
        Snapshot(window, "vpnpath");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Changes[1].Label.ShouldBe("DNS");
        page.Changes[0].Label.ShouldBe("Sites see");
    }

    [AvaloniaFact]
    public async Task Proxy_mode_keeps_the_route_and_dns()
    {
        var (_, shell) = Open();
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        await Connect(shell);
        shell.NavigateCommand.Execute(Pages.VpnPath);
        var page = (VpnPathPageViewModel)shell.CurrentPage!;
        await page.RefreshCommand.ExecuteAsync(null);

        page.AfterNote.ShouldStartWith("Режим прокси: туннеля нет");
        Links(page.After).ShouldAllBe(l => !l.IsTunnel);
        page.Changes.Select(c => (c.Before, c.After)).Skip(1).Take(2).ShouldBe([
            ("192.0.2.1", "192.0.2.1"), ("Wi-Fi через 192.0.2.1", "Wi-Fi через 192.0.2.1"),
        ], "прокси не меняет ни DNS ОС, ни маршрут");
        page.Changes[3].After.ShouldBe("поток к серверу от программ с прокси, остальное — как без защиты");
    }

    private static List<PathNodeViewModel> Nodes(PathMapViewModel map) => [.. map.Steps.OfType<PathNodeViewModel>()];

    private static List<PathLinkViewModel> Links(PathMapViewModel map) => [.. map.Steps.OfType<PathLinkViewModel>()];
}
