using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Как устроена сеть» — сетевая лаборатория (этап 8): семь уровней OSI, VPN на уровнях и протоколы; под объяснением — факты
/// машины от службы (в демо — адреса документации), чего нет — «не измерено»; «Эксперт» добавляет подробности и факты и
/// запоминается. Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class LayersPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Layers_from_the_check_page_explain_with_this_machine_facts()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "LayersButton");
        var page = shell.CurrentPage.ShouldBeOfType<LayersPageViewModel>();
        WaitFor(() => Facts(page.Layers[6]).Any(f => f.IsMeasured));
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.IsExpert.ShouldBeFalse("по умолчанию — для начинающего");

        page.Layers.Select(l => (l.Badge, l.Title)).ShouldBe([
            ("7", "Прикладной"), ("6", "Представления"), ("5", "Сеансовый"), ("4", "Транспортный"), ("3", "Сетевой"), ("2", "Канальный"),
            ("1", "Физический"),
        ]);
        page.Layers.ShouldAllBe(l => !l.HasExpert);
        Facts(page.Layers[0]).ShouldBe([("DNS", "192.0.2.1", true), ("Сайты видят", "198.51.100.7", true)]);
        Facts(page.Layers[1]).ShouldBe([("TLS", "не измерено", false)], "TLS сайта еще не мерили");
        Facts(page.Layers[3]).ShouldBe([("TCP установлено", "3", true), ("UDP", "2", true)]);
        Facts(page.Layers[4]).ShouldBe([("Адрес в сети", "192.0.2.23/24", true), ("Шлюз", "192.0.2.1", true)]);
        Facts(page.Layers[6]).ShouldBe([("Адаптер", "Wi-Fi · Wi-Fi", true), ("Скорость канала", "866 Мбит/с", true)]);
        page.Topics.Select(t => t.Title).ShouldBe(["IP-адрес", "TCP и UDP", "DNS", "TLS", "HTTP и HTTPS", "TUN", "Маршрутизация"]);
        Facts(page.Topics[5]).ShouldBe([("Туннель", "нет — защита выключена или в режиме прокси", true)]);
        Facts(page.Tunnel!).ShouldBe([("Сейчас", "защита выключена — пакеты идут как есть", true)]);

        await page.MeasureCommand.ExecuteAsync(null);

        page.HasError.ShouldBeFalse();
        Facts(page.Layers[1]).ShouldBe([("TLS", "TLS 1.3 · TLS_AES_128_GCM_SHA256", true)]);
        Facts(page.Layers[2]).ShouldBe([("Протокол поверх TLS", "h2", true)]);
        Facts(page.Topics[2]).ShouldBe([("DNS", "192.0.2.1", true), ("Адрес сайта", "203.0.113.80, 2001:db8::80", true)]);
        Facts(page.Topics[3])[1].ShouldBe(("Сертификат", "в порядке", true));

        await Connect(shell);
        await page.RefreshCommand.ExecuteAsync(null);
        // Переключатель — сегмент «Эксперт»: нажатие ставит отметку, привязка меняет настройку.
        window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "ExpertMode").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        page.IsExpert.ShouldBeTrue();
        Service<SettingsService>().Current.LabExpert.ShouldBeTrue("подробность объяснений запоминается");
        page.Layers.ShouldAllBe(l => l.HasExpert);
        Facts(page.Layers[0]).ShouldBe([
            ("DNS", "172.19.0.2 — через туннель", true), ("Сайты видят", "203.0.113.24", true), ("Реальный адрес", "198.51.100.7", true),
        ]);
        Facts(page.Layers[4]).Select(f => f.Label).ShouldBe(["Адрес в сети", "Шлюз", "IPv6", "Маршрутов", "По умолчанию", "Через туннель"]);
        Facts(page.Layers[4])[5].ShouldBe(("Через туннель", "0.0.0.0/1, 128.0.0.0/1", true));
        Facts(page.Topics[5]).ShouldBe([
            ("Туннель", "Private Gateway · 172.19.0.1/30", true), ("MTU туннеля", "1420 байт", true), ("Через туннель", "0.0.0.0/1, 128.0.0.0/1", true),
        ]);
        Facts(page.Tunnel!)[0].Value.ShouldBe("туннель включен — так идет весь трафик машины");
        page.Tunnel!.Expert.ShouldStartWith("Внешний пакет больше внутреннего");
        Snapshot(window, "layers");

        page.Site = "nxdomain.example";
        await page.MeasureCommand.ExecuteAsync(null);
        Facts(page.Topics[2])[1].ShouldBe(("Адрес сайта", "не найдено", true));

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Layers[0].Title.ShouldBe("Application");
        Facts(page.Layers[0])[0].Value.ShouldBe("172.19.0.2 — through the tunnel");
        page.MeasureText.ShouldBe("Measure");
    }

    [AvaloniaFact]
    public async Task Without_a_service_every_fact_is_not_measured()
    {
        var (_, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Layers);
        var page = (LayersPageViewModel)shell.CurrentPage!;
        await page.RefreshCommand.ExecuteAsync(null);

        page.Layers.Concat(page.Topics).SelectMany(Facts).ShouldAllBe(f => !f.IsMeasured && f.Value == "не измерено");

        await page.MeasureCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Служба не ответила — замеры делает она. Запустите службу и повторите.");

        page.Site = "https://example.com/";
        await page.MeasureCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Введите IP или имя узла, например example.com.");
    }

    private static List<(string Label, string Value, bool IsMeasured)> Facts(LabCardViewModel card) =>
        [.. card.Facts.Select(f => (f.Label, f.Value, f.IsMeasured))];
}
