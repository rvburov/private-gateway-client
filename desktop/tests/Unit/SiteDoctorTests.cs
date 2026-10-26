using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Lab;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// «Почему сайт не открывается» (лаборатория, этап 8): шаги в порядке запроса браузера, первый неудачный — вывод, следующие
/// — «не проверялось»; без замера вывода нет.
/// </summary>
public sealed class SiteDoctorTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Router = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress SiteIp = IPAddress.Parse("203.0.113.80");

    private static readonly NetworkSnapshot Snapshot = new(At,
        [new NetworkCard("wlan0", "wlan0", AdapterKind.Wifi, true, null, 1500, null, [new(IPAddress.Parse("192.0.2.23"), 24)], [Router], [Router], null)],
        [new NetworkRoute("default", Router, "wlan0", 600)]);

    private static readonly ConnectionStatus Off = ConnectionStatus.Initial(At);

    private static readonly ProbeStep[] Dns = [new(1, SiteIp, TimeSpan.FromMilliseconds(12), ProbeOutcome.Ok)];

    private static readonly ProbeStep Tcp = new(1, SiteIp, TimeSpan.FromMilliseconds(24), ProbeOutcome.Ok);

    private static readonly TlsFacts Certificate = new("TLS 1.3", "TLS_AES_128_GCM_SHA256", "h2", "CN=example.com", "CN=Example CA",
        At.AddDays(-30), At.AddDays(60), ["example.com"], []);

    private static readonly ProbeStep Tls = new(1, SiteIp, TimeSpan.FromMilliseconds(61), ProbeOutcome.Ok, Certificate);

    private static readonly SiteFacts Healthy = new(Snapshot, Off, Dns, Tcp, Tls);

    [Fact]
    public void Every_step_passing_means_the_site_answers()
    {
        var diagnosis = SiteDoctor.Diagnose(Healthy);

        diagnosis.Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers);
        diagnosis.Steps.Select(s => (s.Kind, s.Result, s.Detail)).ShouldBe([
            (DiagnosisStepKind.Network, DiagnosisResult.Ok, "wlan0 → 192.0.2.1"),
            (DiagnosisStepKind.Protection, DiagnosisResult.Ok, null),
            (DiagnosisStepKind.Dns, DiagnosisResult.Ok, "203.0.113.80"),
            (DiagnosisStepKind.Tcp, DiagnosisResult.Ok, "203.0.113.80"),
            (DiagnosisStepKind.Tls, DiagnosisResult.Ok, "TLS 1.3"),
        ]);
        diagnosis.Steps[3].Time.ShouldBe(TimeSpan.FromMilliseconds(24));
        SiteDoctor.Continue(Healthy).ShouldBeTrue();
    }

    [Fact]
    public void The_first_failed_step_is_the_cause_and_later_ones_are_not_checked()
    {
        var noGateway = Snapshot with { Cards = [Snapshot.Cards[0] with { Gateways = [] }], Routes = [] };
        var diagnosis = SiteDoctor.Diagnose(Healthy with { Snapshot = noGateway });

        diagnosis.Verdict.ShouldBe(DiagnosisVerdict.NoNetwork);
        diagnosis.Steps.Select(s => s.Result).ShouldBe([
            DiagnosisResult.Failed, DiagnosisResult.Skipped, DiagnosisResult.Skipped, DiagnosisResult.Skipped, DiagnosisResult.Skipped,
        ], "замеры после причины в вывод не идут, даже если были");
        diagnosis.Steps.Skip(1).ShouldAllBe(s => s.Detail == null && s.Outcome == null);
        diagnosis.Steps[0].Detail.ShouldBeNull("адаптера выхода нет");
        SiteDoctor.Continue(new SiteFacts(noGateway, Off)).ShouldBeFalse();

        var ppp = new NetworkSnapshot(At,
            [new NetworkCard("wwan0", "wwan0", AdapterKind.Cellular, true, null, 1500, null, [new(IPAddress.Parse("198.51.100.20"), 32)], [], [], null)],
            [new NetworkRoute("default", null, "wwan0", 0)]);
        var cellular = SiteDoctor.Diagnose(Healthy with { Snapshot = ppp });
        cellular.Steps[0].ShouldBe(new DiagnosisStep(DiagnosisStepKind.Network, DiagnosisResult.Ok, "wwan0"), "маршрут по умолчанию без шлюза — выход есть");
        cellular.Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers);
    }

    [Fact]
    public void Protection_that_keeps_the_network_closed_is_the_cause()
    {
        var blocked = Off with { State = ConnectionState.Blocked, ErrorCode = ErrorCodes.ServerNotResponding };
        var diagnosis = SiteDoctor.Diagnose(new SiteFacts(Snapshot, blocked));
        diagnosis.Verdict.ShouldBe(DiagnosisVerdict.ProtectionBlocks);
        diagnosis.Steps[1].Detail.ShouldBe(ErrorCodes.ServerNotResponding);

        SiteDoctor.Diagnose(new SiteFacts(Snapshot, Off with { State = ConnectionState.Connecting })).Steps[1].Detail.ShouldBe("Connecting");
        SiteDoctor.Diagnose(new SiteFacts(Snapshot, Off with { State = ConnectionState.Reconnecting })).Verdict.ShouldBe(DiagnosisVerdict.ProtectionBlocks);
        SiteDoctor.Diagnose(new SiteFacts(Snapshot, Off with { StrictBlock = true })).Verdict.ShouldBe(DiagnosisVerdict.ProtectionBlocks);

        var errorUnderBlock = Off with { State = ConnectionState.Error, Protection = ProtectionChecks.For(ConnectionState.Error) };
        SiteDoctor.Diagnose(new SiteFacts(Snapshot, errorUnderBlock)).Verdict.ShouldBe(DiagnosisVerdict.ProtectionBlocks);
        var errorWithoutBlock = errorUnderBlock with { Protection = errorUnderBlock.Protection with { AllTraffic = CheckState.Off } };
        SiteDoctor.Diagnose(Healthy with { Status = errorWithoutBlock }).Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers, "ошибка без блока сеть не закрывает");
        SiteDoctor.Diagnose(Healthy with { Status = Off with { State = ConnectionState.Connected } }).Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers);
    }

    [Theory]
    [InlineData(ProbeOutcome.NotFound, DiagnosisVerdict.NameNotFound)]
    [InlineData(ProbeOutcome.TimedOut, DiagnosisVerdict.DnsFailed)]
    [InlineData(ProbeOutcome.Failed, DiagnosisVerdict.DnsFailed)]
    public void Dns_without_an_address_is_the_cause(ProbeOutcome outcome, DiagnosisVerdict verdict)
    {
        var diagnosis = SiteDoctor.Diagnose(Healthy with { Dns = [new ProbeStep(1, null, null, outcome)] });

        diagnosis.Verdict.ShouldBe(verdict);
        (diagnosis.Steps[2].Result, diagnosis.Steps[2].Outcome).ShouldBe((DiagnosisResult.Failed, outcome));
        diagnosis.Steps[3].Result.ShouldBe(DiagnosisResult.Skipped);
        SiteDoctor.Diagnose(Healthy with { Dns = [] }).Verdict.ShouldBe(DiagnosisVerdict.DnsFailed, "пустой ответ — тоже без адреса");
    }

    [Theory]
    [InlineData(ProbeOutcome.Refused, DiagnosisVerdict.PortRefused)]
    [InlineData(ProbeOutcome.TimedOut, DiagnosisVerdict.NoAnswer)]
    [InlineData(ProbeOutcome.Failed, DiagnosisVerdict.NoAnswer)]
    [InlineData(ProbeOutcome.Unreachable, DiagnosisVerdict.Unreachable)]
    public void Connection_failures_are_told_apart(ProbeOutcome outcome, DiagnosisVerdict verdict)
    {
        var diagnosis = SiteDoctor.Diagnose(Healthy with { Tcp = new ProbeStep(1, SiteIp, null, outcome) });

        diagnosis.Verdict.ShouldBe(verdict);
        diagnosis.Steps[3].Outcome.ShouldBe(outcome);
        diagnosis.Steps[4].Result.ShouldBe(DiagnosisResult.Skipped);
    }

    [Fact]
    public void A_bad_certificate_or_a_broken_handshake_is_the_cause()
    {
        var bad = Tls with { Tls = Certificate with { Problems = [TlsProblem.Expired, TlsProblem.NameMismatch] } };
        var diagnosis = SiteDoctor.Diagnose(Healthy with { Tls = bad });
        diagnosis.Verdict.ShouldBe(DiagnosisVerdict.Certificate);
        diagnosis.Steps[4].Problems.ShouldBe([TlsProblem.Expired, TlsProblem.NameMismatch]);

        var broken = SiteDoctor.Diagnose(Healthy with { Tls = new ProbeStep(1, SiteIp, null, ProbeOutcome.Failed) });
        broken.Verdict.ShouldBe(DiagnosisVerdict.TlsFailed);
        broken.Steps[4].Outcome.ShouldBe(ProbeOutcome.Failed);
    }

    [Fact]
    public void Without_measurements_there_is_no_verdict_about_the_site()
    {
        var none = SiteDoctor.Diagnose(new SiteFacts(null, Off));
        none.Verdict.ShouldBe(DiagnosisVerdict.NoService);
        none.Steps.ShouldAllBe(s => s.Result == DiagnosisResult.Skipped);

        SiteDoctor.Diagnose(new SiteFacts(Snapshot, Off, Dns, Tcp)).Verdict.ShouldBe(DiagnosisVerdict.NoService, "до TLS не дошли без причины — замера нет");
        var noSnapshot = SiteDoctor.Diagnose(Healthy with { Snapshot = null });
        noSnapshot.Steps[0].Result.ShouldBe(DiagnosisResult.Skipped, "сеть машины служба не дала");
        noSnapshot.Verdict.ShouldBe(DiagnosisVerdict.SiteAnswers, "сайт ответил — это замер");
    }
}
