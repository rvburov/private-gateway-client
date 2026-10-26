using System.Net;
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
/// «До и после VPN» — сетевой инспектор (этап 7): открывается из «Проверки», «до» снимает служба при включении защиты, вывод по
/// каждой строке — сменился, утечка, защита выключена, не с чем сравнить; под строками — методика. Снимки — в
/// artifacts/ui-snapshots.
/// </summary>
public sealed class ComparePageTests : WindowHarness
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly NetworkFacts Real = new(At, "198.51.100.7", "2001:db8:1::7", new IPEndPoint(IPAddress.Parse("198.51.100.7"), 51820),
        "Wi-Fi", IPAddress.Parse("192.0.2.1"), [IPAddress.Parse("192.0.2.1")]);

    [AvaloniaFact]
    public async Task Before_and_after_the_connect_every_row_changes_and_says_so()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        Press(window, "CompareButton");
        WaitFor(() => shell.CurrentPage is ComparePageViewModel { IsLoading: false, HasRows: true });
        var page = (ComparePageViewModel)shell.CurrentPage!;
        shell.IsCheck.ShouldBeTrue("подстраница «Проверки»");
        page.BeforeText.ShouldStartWith("Снимка «до» нет");
        page.Rows.ShouldAllBe(r => r.VerdictText == "защита выключена");

        await Connect(shell);
        await page.RunCommand.ExecuteAsync(null);

        page.BeforeText.ShouldStartWith("До — снято при включении защиты");
        page.Rows.Select(r => (r.Label, r.Before, r.Now, r.VerdictText)).ShouldBe([
            ("IP для сайтов", "198.51.100.7", "203.0.113.24", "сменился"),
            ("IPv6 для сайтов", "2001:db8:1::7", "нет ответа", "сменился"),
            ("WebRTC (UDP)", "198.51.100.7", "203.0.113.24", "сменился"),
            ("Выход в интернет", "Wi-Fi через 192.0.2.1", "Private Gateway", "сменился"),
            ("DNS", "192.0.2.1", "172.19.0.2", "сменился"),
        ]);
        page.Rows.ShouldAllBe(r => r.IsGood);
        page.Methods.Select(m => m.Label).ShouldBe(["IP для сайтов", "IPv6 для сайтов", "WebRTC (UDP)", "Выход в интернет", "DNS"]);
        page.Methods.Single(m => m.Label == "WebRTC (UDP)").Value.ShouldContain("в режиме прокси шлет UDP WebRTC мимо прокси");
        Snapshot(window, "compare");

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        page.Rows[0].Label.ShouldBe("IP seen by sites");
        page.Rows[0].VerdictText.ShouldBe("changed");
    }

    [AvaloniaFact]
    public void Real_address_seen_under_protection_is_a_leak()
    {
        var proxy = new NetworkFacts(At.AddMinutes(5), "203.0.113.24", "2001:db8:1::7", new IPEndPoint(IPAddress.Parse("198.51.100.7"), 50000),
            "Wi-Fi", IPAddress.Parse("192.0.2.1"), [IPAddress.Parse("192.0.2.1")]);

        var rows = ComparePageViewModel.Compare(new NetworkComparison(Real with { Webrtc = null }, proxy), protectedNow: true).ToDictionary(r => r.Key);

        rows["ip"].Verdict.ShouldBe(CompareVerdict.Changed);
        rows["ipv6"].Verdict.ShouldBe(CompareVerdict.Leak, "IPv6 тот же — идет мимо защиты");
        rows["webrtc"].Verdict.ShouldBe(CompareVerdict.Leak, "STUN видит реальный адрес из снимка «до»");
        rows["webrtc"].IsLeak.ShouldBeTrue();
        rows["route"].Verdict.ShouldBe(CompareVerdict.Same, "режим прокси маршрут не меняет");
        rows["dns"].Verdict.ShouldBe(CompareVerdict.Same);
        rows["ip"].VerdictText.ShouldBe("сменился");
        rows["webrtc"].VerdictText.ShouldBe("виден реальный");
    }

    [AvaloniaFact]
    public void Without_a_before_snapshot_only_the_real_ip_measured_later_shows_a_leak()
    {
        var now = new NetworkFacts(At, "203.0.113.24", null, new IPEndPoint(IPAddress.Parse("198.51.100.7"), 50000), null, null, []);

        var unknown = ComparePageViewModel.Compare(new NetworkComparison(null, now), protectedNow: true).ToDictionary(r => r.Key);
        unknown.Values.ShouldAllBe(r => r.Verdict == CompareVerdict.Unknown, "без «до» сравнить не с чем");
        unknown["ip"].Before.ShouldBe("не измерено");

        ComparePageViewModel.Compare(new NetworkComparison(null, now), protectedNow: true, directIp: "198.51.100.7")
            .Single(r => r.Key == "webrtc").Verdict.ShouldBe(CompareVerdict.Leak, "реальный адрес служба измерила и после подключения (режим прокси)");
        ComparePageViewModel.Compare(new NetworkComparison(Real, Real with { Ipv6 = null }), protectedNow: false)
            .ShouldAllBe(r => r.Verdict == CompareVerdict.Off);
        foreach (var verdict in Enum.GetValues<CompareVerdict>())
        {
            Loc.Instance.Has("compare.verdict." + verdict.ToString().ToLowerInvariant()).ShouldBeTrue(verdict.ToString());
        }
    }

    [AvaloniaFact]
    public void Without_a_service_the_page_says_so()
    {
        var (window, shell) = Open(gateway: sp =>
            new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.None));
        shell.NavigateCommand.Execute(Pages.Compare);
        WaitFor(() => shell.CurrentPage is ComparePageViewModel { IsLoading: false, IsUnavailable: true });
        var page = (ComparePageViewModel)shell.CurrentPage!;

        page.HasRows.ShouldBeFalse();
        page.Methods.ShouldNotBeEmpty("методика видна и без службы");
        Snapshot(window, "compare-no-service");
    }
}
