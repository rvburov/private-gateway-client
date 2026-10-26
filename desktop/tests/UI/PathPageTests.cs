using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
/// «Путь до сайта» — сетевая лаборатория (этап 8): открывается из «Проверки», звенья и участки — по сведениям службы (в демо —
/// адреса документации), каждое помечено «измерено» или «условно»; проверка сайта и поиск провайдеров превращают условное в
/// измеренное; при защите в режиме VPN участки до сервера — в туннеле. Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class PathPageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Map_from_the_check_page_turns_assumptions_into_measurements()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "PathButton");
        var page = shell.CurrentPage.ShouldBeOfType<PathPageViewModel>();
        WaitFor(() => Nodes(page).Any(n => n.Title == "Wi-Fi"));
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.Note.ShouldStartWith("Защита выключена");

        Nodes(page).Select(n => (n.Title, n.Subtitle, n.EvidenceText)).ShouldBe([
            ("Этот компьютер", "Здесь начинается путь", "измерено"),
            ("Wi-Fi", "Wi-Fi · 192.0.2.23", "измерено"),
            ("Роутер", "192.0.2.1", "измерено"),
            ("Провайдер", "198.51.100.7", "измерено"),
            ("example.com", "Не проверен", "условно"),
        ]);
        Links(page).Select(l => (l.Text, l.IsMeasured)).ShouldBe([
            ("Внутри компьютера: программа → сетевой стек ОС", true),
            ("По радио Wi-Fi до роутера", true),
            ("Сеть провайдера", false),
            ("Интернет до сайта", false),
        ]);
        page.Summary.ShouldBe("Измерено 6 из 9: звенья и участки");
        Links(page).ShouldAllBe(l => !l.IsTunnel);

        await page.CheckCommand.ExecuteAsync(null);

        page.HasError.ShouldBeFalse();
        Nodes(page)[^1].Subtitle.ShouldBe("203.0.113.80");
        Nodes(page)[^1].IsMeasured.ShouldBeTrue("сайт ответил на рукопожатие TLS");
        Links(page).Select(l => l.Text).ShouldBe([
            "Внутри компьютера: программа → сетевой стек ОС",
            "По радио Wi-Fi до роутера · 1,8 мс",
            "Сеть провайдера · ответили узлов: 4",
            "Интернет до сайта · 61,3 мс",
        ]);
        Links(page).ShouldAllBe(l => l.IsMeasured);
        page.IsExpert = true;
        Links(page)[2].Expert.ShouldBe("узлы: 198.51.100.1 → * → 198.51.100.77 → 203.0.113.80", "эксперту — узлы трассировки за роутером");
        page.IsExpert = false;

        await Connect(shell);
        await page.LookupCommand.ExecuteAsync(null);

        page.Note.ShouldStartWith("Режим VPN");
        Nodes(page).Select(n => n.Title).ShouldBe(["Этот компьютер", "Wi-Fi", "Роутер", "Example Broadband", "Example Hosting B.V.", "example.com"]);
        Nodes(page)[3].Subtitle.ShouldBe("AS64496 · 198.51.100.7");
        Nodes(page)[4].IsMeasured.ShouldBeTrue("адрес выхода измерен");
        Links(page).Select(l => l.IsTunnel).ShouldBe([false, true, true, true, false]);
        Links(page)[0].Text.ShouldBe("Внутри компьютера: программа → TUN-адаптер → ядро шифрует");
        Links(page)[2].IsMeasured.ShouldBeFalse("трассировка в туннеле сеть провайдера не меряет");
        Links(page)[1].TunnelText.ShouldBe("в туннеле — зашифровано");
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<Line>().Count(l => l.Classes.Contains("conditional") && l.Classes.Contains("link"))
            .ShouldBeGreaterThan(1, "условные участки — пунктиром (и в легенде)");
        var packets = window.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("packet")).ToList();
        packets.Count.ShouldBe(Links(page).Count, "у каждого участка — свой пакет");
        packets.ShouldAllBe(e => !e.Classes.Contains("moving") && !e.IsVisible, "пакеты бегут только во время проверки");
        Nodes(page).ShouldAllBe(n => !n.HasExpert, "для начинающего — без подробностей");
        Snapshot(window, "path");

        window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "ExpertMode").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        page.IsExpert.ShouldBeTrue();
        Nodes(page).Select(n => n.Expert).ShouldBe([null, "MTU 1500 · 866 Мбит/с", null, "сеть 198.51.100.0/24", "сеть 203.0.113.0/24", "TLS 1.3 · h2"]);
        Links(page).ShouldAllBe(l => !l.HasExpert, "в туннеле сеть провайдера трассировкой не измерена — узлов не показываем");
        Snapshot(window, "path-expert");
        window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == "BeginnerMode").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Nodes(page).ShouldAllBe(n => !n.HasExpert);

        page.Site = "nxdomain.example";
        Nodes(page)[^1].Subtitle.ShouldBe("Не проверен", "другой сайт — прежняя проверка к нему не относится");
        await page.CheckCommand.ExecuteAsync(null);
        Nodes(page)[^1].Subtitle.ShouldBe("не найдено");
        Nodes(page)[^1].IsMeasured.ShouldBeFalse();

        page.Site = "https://example.com/";
        await page.CheckCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Введите IP или имя узла, например example.com.");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Note.ShouldStartWith("VPN mode");
        Nodes(page)[0].Title.ShouldBe("This computer");
        page.CheckText.ShouldBe("Check the site");
    }

    [AvaloniaFact]
    public async Task Without_a_service_the_path_is_assumed_and_the_check_says_why()
    {
        var (_, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Path);
        var page = (PathPageViewModel)shell.CurrentPage!;
        await page.RefreshCommand.ExecuteAsync(null);

        Nodes(page).Skip(1).ShouldAllBe(n => !n.IsMeasured, "служба ничего не рассказала");
        Nodes(page)[1].Subtitle.ShouldBe("Служба не назвала адаптер выхода");

        await page.CheckCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Служба не ответила — замеры делает она. Запустите службу и повторите.");
    }

    private static List<PathNodeViewModel> Nodes(PathPageViewModel page) => [.. page.Steps.OfType<PathNodeViewModel>()];

    private static List<PathLinkViewModel> Links(PathPageViewModel page) => [.. page.Steps.OfType<PathLinkViewModel>()];
}
