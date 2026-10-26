using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Проверка узла» — замеры инспектора (этап 7): открывается из «Проверки», меряет служба (в демо — выдуманные ответы на
/// адресах документации), строки — по мере ответа, итог одной строкой, у TLS — сертификат; под замером — как он устроен
/// и каким путем идет. Снимки — в artifacts/ui-snapshots.
/// </summary>
public sealed class ProbePageTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Ping_from_the_check_page_shows_every_echo_and_a_summary()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "ProbeButton");
        var page = shell.CurrentPage.ShouldBeOfType<ProbePageViewModel>();
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.Kind.ShouldBe(ProbeKind.Ping);
        page.NeedsPort.ShouldBeFalse();
        page.Method.ShouldStartWith("Четыре эхо ICMP");
        page.HasPathNote.ShouldBeFalse("защита выключена — замер идет как обычно");

        page.Target = " Example.com ";
        await page.RunCommand.ExecuteAsync(null);

        page.Rows.Select(r => (r.Label, r.Address, r.Result)).ShouldBe([
            ("1", "203.0.113.80", "24,1 мс"), ("2", "203.0.113.80", "23,6 мс"), ("3", "203.0.113.80", "25,3 мс"), ("4", "203.0.113.80", "23,9 мс"),
        ]);
        page.Summary.ShouldBe("Отправлено 4 · получено 4 · потери 0 % · мин/сред/макс 23,6/24,2/25,3 мс");
        page.IsRunning.ShouldBeFalse();
        Snapshot(window, "probe-ping");
    }

    [AvaloniaFact]
    public async Task Route_marks_a_silent_router_and_tls_shows_the_certificate()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Probe);
        var page = (ProbePageViewModel)shell.CurrentPage!;
        page.Target = "example.com";

        page.Kinds.Single(k => k.Kind == ProbeKind.Trace).IsSelected = true;
        await page.RunCommand.ExecuteAsync(null);
        page.Rows.Select(r => (r.Label, r.Address, r.Result, r.IsOk)).ShouldBe([
            ("1", "192.0.2.1", "1,8 мс", true),
            ("2", "198.51.100.1", "7,4 мс", true),
            ("3", "*", "нет ответа", false),
            ("4", "198.51.100.77", "19,2 мс", true),
            ("5", "203.0.113.80", "23,8 мс", true),
        ]);
        page.Summary.ShouldBe("Узел ответил на участке 5");
        page.Method.ShouldContain("Звездочка — роутер промолчал");

        page.Kinds.Single(k => k.Kind == ProbeKind.Tls).IsSelected = true;
        page.Kinds.Single(k => k.Kind == ProbeKind.Trace).IsSelected.ShouldBeFalse("выбран один замер");
        page.NeedsPort.ShouldBeTrue();
        await page.RunCommand.ExecuteAsync(null);
        page.Summary.ShouldBe("TLS 1.3 · сертификат в порядке");
        page.TlsRows.Select(r => r.Label).ShouldBe(["Протокол", "Шифр", "ALPN", "Кому", "Кем выдан", "Действует", "Имена", "Проверка"]);
        page.TlsRows.Single(r => r.Label == "Действует").Value.ShouldEndWith("осталось 60 дн.");
        page.TlsRows.Single(r => r.Label == "Проверка").Value.ShouldBe("в порядке");
        Snapshot(window, "probe-tls");

        page.PortText = "22";
        await page.RunCommand.ExecuteAsync(null);
        page.Summary.ShouldBe("Порт 22 закрыт: узел отказал в соединении");
        page.HasTls.ShouldBeFalse("рукопожатия не было — и сертификата нет");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Summary.ShouldBe("Port 22 is closed: the host refused the connection");
        page.Kinds.Select(k => k.Label).ShouldBe(["Ping", "Route", "DNS", "TCP", "TLS"]);
    }

    [AvaloniaFact]
    public async Task Wrong_input_is_named_before_asking_the_service()
    {
        var (_, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Probe);
        var page = (ProbePageViewModel)shell.CurrentPage!;

        page.Target = "https://example.com/";
        await page.RunCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Введите IP или имя узла, например example.com.");
        page.HasRows.ShouldBeFalse();

        page.Target = "example.com";
        page.Kinds.Single(k => k.Kind == ProbeKind.Tcp).IsSelected = true;
        page.PortText = "70000";
        await page.RunCommand.ExecuteAsync(null);
        page.Error.ShouldBe("Порт — число от 1 до 65535.");

        page.PortText = "443";
        await page.RunCommand.ExecuteAsync(null);
        page.HasError.ShouldBeFalse();
        page.Summary.ShouldBe("Порт 443 открыт · соединение 24,6 мс");

        page.Target = "nxdomain.example";
        await page.RunCommand.ExecuteAsync(null);
        page.Summary.ShouldBe("Имя не найдено");
    }

    [AvaloniaFact]
    public async Task With_protection_on_the_page_says_which_way_the_probe_goes()
    {
        var (_, shell) = Open();
        await Connect(shell);
        shell.NavigateCommand.Execute(Pages.Probe);
        var page = (ProbePageViewModel)shell.CurrentPage!;

        page.PathNote.ShouldStartWith("Защита включена: эхо идет в туннель");
        page.Kinds.Single(k => k.Kind == ProbeKind.Tcp).IsSelected = true;
        page.PathNote.ShouldBe("Защита включена: замер идет через туннель и сервер.");
    }

    [AvaloniaFact]
    public void Certificate_problems_and_an_expired_date_are_named()
    {
        var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var tls = new TlsFacts("TLS 1.2", "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", null, "CN=example.com", "CN=example.com",
            now.AddDays(-400), now.AddDays(-35), [], [TlsProblem.Expired, TlsProblem.Untrusted, TlsProblem.NameMismatch]);

        var rows = ProbePageViewModel.TlsDetails(tls, now).ToDictionary(r => r.Label, r => r.Value);

        rows.Keys.ToList().ShouldBe(["Протокол", "Шифр", "Кому", "Кем выдан", "Действует", "Проверка"], customMessage: "без ALPN и имен — без пустых строк");
        rows["Действует"].ShouldStartWith("истек ");
        rows["Проверка"].ShouldBe("срок кончился\nнет доверенного корня: самоподписанный или чужой центр\nвыдан на другое имя");
        foreach (var problem in Enum.GetValues<TlsProblem>())
        {
            Loc.Instance.Has("probe.tls.problem." + problem.ToString().ToLowerInvariant()).ShouldBeTrue(problem.ToString());
        }

        foreach (var outcome in Enum.GetValues<ProbeOutcome>())
        {
            Loc.Instance.Has("probe.outcome." + outcome.ToString().ToLowerInvariant()).ShouldBeTrue(outcome.ToString());
        }

        ProbePageViewModel.Summarize(ProbeKind.Ping, 0, [new ProbeStep(1, null, null, ProbeOutcome.TimedOut), new ProbeStep(2, null, null, ProbeOutcome.TimedOut)])
            .ShouldBe("Отправлено 2 — ни одного ответа");
        ProbePageViewModel.Summarize(ProbeKind.Ping, 0, [
            new ProbeStep(1, null, TimeSpan.FromMilliseconds(120), ProbeOutcome.Ok), new ProbeStep(2, null, null, ProbeOutcome.TimedOut),
            new ProbeStep(3, null, TimeSpan.FromMilliseconds(140), ProbeOutcome.Ok)])
            .ShouldBe("Отправлено 3 · получено 2 · потери 33 % · мин/сред/макс 120/130/140 мс");
        ProbePageViewModel.Summarize(ProbeKind.Trace, 0, [new ProbeStep(1, null, TimeSpan.FromMilliseconds(2), ProbeOutcome.Hop), new ProbeStep(2, null, null, ProbeOutcome.Unreachable)])
            .ShouldBe("Узел недоступен — так ответил участок 2");
        ProbePageViewModel.Summarize(ProbeKind.Trace, 0, [new ProbeStep(30, null, null, ProbeOutcome.TimedOut)]).ShouldBe("Узел не ответил, пройдено участков: 30");
        ProbePageViewModel.Summarize(ProbeKind.Tls, 443, [new ProbeStep(1, null, TimeSpan.FromMilliseconds(80), ProbeOutcome.Ok, tls)])
            .ShouldBe("Сертификат с ошибками — подробности ниже");
        ProbePageViewModel.Summarize(ProbeKind.Tcp, 443, [new ProbeStep(1, null, null, ProbeOutcome.TimedOut)]).ShouldBe("Нет ответа за 5 с");
    }

    [AvaloniaFact]
    public async Task Without_a_service_the_page_says_so()
    {
        var (window, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Probe);
        var page = (ProbePageViewModel)shell.CurrentPage!;

        page.Target = "203.0.113.10";
        await page.RunCommand.ExecuteAsync(null);

        page.Error.ShouldBe("Служба не ответила — замеры делает она. Запустите службу и повторите.");
        Dispatcher.UIThread.RunJobs();
        Find<TextBlock>(window, "ProbeError").IsVisible.ShouldBeTrue();
        Snapshot(window, "probe-no-service");
        page.HasRows.ShouldBeFalse();
    }
}
