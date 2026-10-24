using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

namespace PrivateGateway.Client.Tests.Unit;

public class SimulatedGatewayClientTests
{
    private static readonly ServerProfile Server =
        new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    [Fact]
    public async Task Connect_goes_through_connecting_to_connected()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-24T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var gateway = new SimulatedGatewayClient(time);
        var seen = new List<ConnectionState>();
        gateway.StatusChanged += (_, s) => seen.Add(s.State);

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Connecting);
        gateway.Status.ServerId.ShouldBe("nl");

        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        seen.ShouldBe([ConnectionState.Connecting, ConnectionState.Connected]);
        var status = gateway.Status;
        status.Since.ShouldBe(time.GetUtcNow());
        status.Exit.Ip.ShouldBe("203.0.113.24");
        status.Exit.DirectIp.ShouldBe("198.51.100.7");
        status.Traffic.LatencyMs.ShouldBe(42);
        status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Connected) with { CheckedAt = time.GetUtcNow() });
    }

    [Fact]
    public async Task Server_that_does_not_answer_ends_in_block_without_exit()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);

        var connect = gateway.ConnectAsync(Server with { Id = "tr", LatencyMs = null }, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        var status = gateway.Status;
        status.State.ShouldBe(ConnectionState.Blocked);
        status.ServerId.ShouldBe("tr", "другой сервер сам не выбирается");
        status.Exit.Ip.ShouldBeNull();
        status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Blocked));
    }

    [Fact]
    public async Task Disconnect_while_connecting_cancels_and_stays_off()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        gateway.Status.State.ShouldBe(ConnectionState.Off);
        gateway.Status.ServerId.ShouldBeNull();
        gateway.Status.Exit.Ip.ShouldBeNull();
    }

    [Fact]
    public async Task Disconnect_when_already_off_raises_nothing()
    {
        using var gateway = new SimulatedGatewayClient(new FakeTimeProvider());
        var raised = 0;
        gateway.StatusChanged += (_, _) => raised++;

        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        raised.ShouldBe(0);
    }

    [Fact]
    public async Task Checks_update_time_only_when_connected()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);

        await RunChecks(gateway, time);
        gateway.Status.Protection.CheckedAt.ShouldBeNull();

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        time.Advance(TimeSpan.FromMinutes(5));
        await RunChecks(gateway, time);

        gateway.Status.Protection.CheckedAt.ShouldBe(time.GetUtcNow());
    }

    [Fact]
    public void Status_moves_only_by_allowed_transitions()
    {
        var off = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);

        Should.Throw<InvalidOperationException>(() => off.MoveTo(ConnectionState.Connected, DateTimeOffset.UnixEpoch));
        off.MoveTo(ConnectionState.Connecting, DateTimeOffset.UnixEpoch)
            .MoveTo(ConnectionState.Connected, DateTimeOffset.UnixEpoch)
            .MoveTo(ConnectionState.Reconnecting, DateTimeOffset.UnixEpoch)
            .Attempt.ShouldBe(1);
    }

    [Fact]
    public async Task Report_follows_the_state_and_says_it_is_a_simulation()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);

        var off = await RunChecks(gateway, time);
        off.Results.Select(r => r.Check).ShouldBe(Enum.GetValues<DiagnosticCheck>());
        off[DiagnosticCheck.Service].ShouldBe(new DiagnosticResult(DiagnosticCheck.Service, CheckState.Warning, "simulated"));
        off[DiagnosticCheck.AllTraffic]!.State.ShouldBe(CheckState.Off);
        off[DiagnosticCheck.ExitIp]!.State.ShouldBe(CheckState.Off);

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        var on = await RunChecks(gateway, time);
        on.Results.Where(r => r.Check != DiagnosticCheck.Service).ShouldAllBe(r => r.State == CheckState.Ok);
        on.FinishedAt.ShouldBe(time.GetUtcNow());

        var blocked = gateway.ConnectAsync(Server with { Id = "tr", LatencyMs = null }, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await blocked;
        var stop = await RunChecks(gateway, time);
        stop[DiagnosticCheck.AllTraffic]!.State.ShouldBe(CheckState.Stopped);
        stop[DiagnosticCheck.KillSwitch]!.State.ShouldBe(CheckState.Ok, "блок при обрыве держит трафик");
        stop[DiagnosticCheck.Core].ShouldBe(new DiagnosticResult(DiagnosticCheck.Core, CheckState.Stopped, "no_reply"));
    }

    [Fact]
    public async Task Journal_records_connection_events_without_the_network_name()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);
        var added = new List<JournalEntry>();
        gateway.JournalAdded += (_, e) => added.Add(e);

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        added.Select(e => e.Message).ShouldBe(
        [
            "connect  server «Нидерланды 01»",
            "route v4/v6 default -> tun",
            "killswitch on  allow=203.0.113.24",
            "tunnel up  server «Нидерланды 01»",
            "tunnel down  route restored  killswitch off",
        ]);
        gateway.Journal.Count.ShouldBe(2 + added.Count);
        gateway.Journal.ShouldNotContain(e => e.Message.Contains("Home", StringComparison.Ordinal), "имя Wi-Fi не пишется");
    }

    [Fact]
    public async Task Journal_keeps_only_the_latest_entries()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);

        for (var i = 0; i < SimulatedGatewayClient.JournalLimit; i++)
        {
            await RunChecks(gateway, time);
        }

        gateway.Journal.Count.ShouldBe(SimulatedGatewayClient.JournalLimit);
        gateway.Journal[0].Message.ShouldStartWith("checks done", Case.Sensitive, "самые старые записи вытеснены");
    }

    private static async Task<DiagnosticReport> RunChecks(SimulatedGatewayClient gateway, FakeTimeProvider time)
    {
        var run = gateway.RunChecksAsync(TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ChecksDelay);
        return await run;
    }

    [Fact]
    public async Task Captive_pause_opens_direct_traffic_for_a_while_and_ends_by_itself()
    {
        var time = new FakeTimeProvider();
        using var gateway = await Connected(time);
        var seen = new List<ConnectionStatus>();
        gateway.StatusChanged += (_, s) => seen.Add(s);

        await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        var paused = gateway.Status;
        paused.IsPaused.ShouldBeTrue();
        paused.CaptivePauseUntil.ShouldBe(time.GetUtcNow() + TimeSpan.FromMinutes(5));
        paused.Protection.ShouldBe(new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Warning, CheckState.Warning, null));
        gateway.Journal.ShouldContain(e => e.Message.StartsWith("captive pause  5 min", StringComparison.Ordinal));

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        gateway.Status.IsPaused.ShouldBeTrue();
        time.Advance(TimeSpan.FromSeconds(1));

        gateway.Status.IsPaused.ShouldBeFalse("защита возвращается сама");
        gateway.Status.State.ShouldBe(ConnectionState.Connected);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Ok);
        seen.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Resume_returns_protection_before_the_pause_ends()
    {
        var time = new FakeTimeProvider();
        using var gateway = await Connected(time);
        await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await gateway.ResumeProtectionAsync(TestContext.Current.CancellationToken);

        gateway.Status.IsPaused.ShouldBeFalse();
        gateway.Journal[^1].Message.ShouldBe("captive pause end (user)  killswitch on");
        time.Advance(TimeSpan.FromMinutes(5));
        gateway.Journal.Count(e => e.Message.StartsWith("captive pause end", StringComparison.Ordinal)).ShouldBe(1, "таймер больше не срабатывает");
    }

    [Fact]
    public async Task Pause_without_protection_does_nothing_and_disconnect_ends_a_pause()
    {
        var time = new FakeTimeProvider();
        using var off = new SimulatedGatewayClient(time);
        await off.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        off.Status.CaptivePauseUntil.ShouldBeNull();

        using var gateway = await Connected(time);
        await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        gateway.Status.CaptivePauseUntil.ShouldBeNull();
        gateway.Status.IsPaused.ShouldBeFalse();
    }

    private static async Task<SimulatedGatewayClient> Connected(FakeTimeProvider time)
    {
        var gateway = new SimulatedGatewayClient(time);
        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        gateway.Status.State.ShouldBe(ConnectionState.Connected);
        return gateway;
    }
}
