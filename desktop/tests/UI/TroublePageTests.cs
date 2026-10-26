using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Lab;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Почему сайт не открывается» — сценарий лаборатории (этап 8): шаги запроса по порядку, замеры службы (в демо — выдуманные
/// ответы на адресах документации), первая причина — вывод с «что делать». Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class TroublePageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task A_working_site_passes_every_step_and_a_missing_name_stops_at_dns()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "TroubleButton");
        var page = shell.CurrentPage.ShouldBeOfType<TroublePageViewModel>();
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.HasResult.ShouldBeFalse();
        page.Via.ShouldStartWith("Защита выключена");

        await page.RunCommand.ExecuteAsync(null);

        page.Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers);
        (page.VerdictTitle, page.VerdictState).ShouldBe(("Сайт отвечает", CheckState.Ok));
        page.VerdictTodo.ShouldStartWith("Обновите страницу");
        page.Steps.Select(s => (s.Title, s.ResultText, s.Detail)).ShouldBe([
            ("Сеть машины", "в порядке", "Wi-Fi → 192.0.2.1"),
            ("Защита", "в порядке", null),
            ("DNS", "в порядке", "203.0.113.80"),
            ("Соединение", "в порядке", "203.0.113.80 · 24,6 мс"),
            ("TLS", "в порядке", "TLS 1.3 · 61,3 мс"),
        ]);
        page.Steps.ShouldAllBe(s => !s.HasExpert);

        page.Site = "nxdomain.example";
        page.IsExpert = true;
        await page.RunCommand.ExecuteAsync(null);

        page.Verdict.ShouldBe(DiagnosisVerdict.NameNotFound);
        (page.VerdictTitle, page.VerdictState).ShouldBe(("Такого имени нет", CheckState.Warning));
        page.Steps.Select(s => (s.ResultText, s.Detail)).ShouldBe([
            ("в порядке", "Wi-Fi → 192.0.2.1"), ("в порядке", null), ("причина", "не найдено"), ("не проверялось", null), ("не проверялось", null),
        ]);
        page.Steps[2].Expert.ShouldStartWith("Служба спрашивает DNS системы");
        Snapshot(window, "trouble");

        page.Site = "https://example.com/";
        await page.RunCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Введите IP или имя узла, например example.com.");

        await Connect(shell);
        page.Via.ShouldStartWith("Режим VPN");
        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.VerdictTitle.ShouldBe("No such name");
        page.Steps[2].ResultText.ShouldBe("cause");
        page.RunText.ShouldBe("Find out");
    }

    [AvaloniaFact]
    public async Task Port_refused_and_no_service_get_their_own_verdicts()
    {
        var (_, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Trouble);
        var page = (TroublePageViewModel)shell.CurrentPage!;

        await page.RunCommand.ExecuteAsync(null);

        page.Verdict.ShouldBe(DiagnosisVerdict.NoService);
        page.VerdictTodo.ShouldBe("Запустите службу и повторите разбор.");
        page.Steps.ShouldAllBe(s => s.ResultText == "не проверялось");
    }
}
