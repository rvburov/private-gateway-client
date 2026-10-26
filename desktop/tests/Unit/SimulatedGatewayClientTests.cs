using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
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
    public async Task Proxy_mode_comes_from_settings_with_partial_checks_and_no_block()
    {
        var time = new FakeTimeProvider();
        var settings = new SettingsService(new MemorySettings());
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        using var gateway = new SimulatedGatewayClient(time, settings);

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        (gateway.Status.Mode, gateway.Status.Protection.AllTraffic).ShouldBe((ConnectionMode.Proxy, CheckState.Waiting));
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        var status = gateway.Status;
        (status.State, status.IsProxy).ShouldBe((ConnectionState.Connected, true));
        status.Protection.ShouldBe(new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Warning, CheckState.Warning, time.GetUtcNow()));
        gateway.Journal.ShouldContain(e => e.Message.Contains("killswitch unavailable", StringComparison.Ordinal));

        connect = gateway.ConnectAsync(Server with { Id = "tr", LatencyMs = null }, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        status = gateway.Status;
        (status.State, status.ErrorCode).ShouldBe((ConnectionState.Error, SimulatedGatewayClient.ProxyServerNotResponding), "перекрывать интернет нечем");
        status.IsProxy.ShouldBeFalse();
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);
        settings.Update(s => s with { Mode = ConnectionMode.Vpn });
        connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        (gateway.Status.Mode, gateway.Status.IsProxy).ShouldBe((ConnectionMode.Vpn, false));
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
    public async Task Strict_mode_disconnect_keeps_the_network_closed_until_strict_mode_is_off()
    {
        var time = new FakeTimeProvider();
        var settings = new SettingsService(new MemorySettings());
        settings.Update(s => s with { Mode = ConnectionMode.Vpn, KillSwitch = true, Protection = s.Protection with { StrictMode = true } });
        using var gateway = new SimulatedGatewayClient(time, settings);
        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        (gateway.Status.State, gateway.Status.StrictBlock).ShouldBe((ConnectionState.Blocked, true), "как у службы: защита выключена, сеть закрыта");

        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        gateway.Status.StrictBlock.ShouldBeTrue("строгий режим действует — повторное отключение сеть не открывает");

        settings.Update(s => s with { Protection = s.Protection with { StrictMode = false } });
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        (gateway.Status.State, gateway.Status.StrictBlock).ShouldBe((ConnectionState.Off, false));
    }

    [Fact]
    public async Task Strict_block_pauses_and_closes_the_network_again_like_the_service()
    {
        var time = new FakeTimeProvider();
        var settings = new SettingsService(new MemorySettings());
        settings.Update(s => s with { Mode = ConnectionMode.Vpn, KillSwitch = true, Protection = s.Protection with { StrictMode = true } });
        using var gateway = new SimulatedGatewayClient(time, settings);
        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        (await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeFalse("пауза уже идет");
        time.Advance(TimeSpan.FromMinutes(5));

        (gateway.Status.State, gateway.Status.StrictBlock, gateway.Status.IsPaused).ShouldBe((ConnectionState.Blocked, true, false));
        gateway.Status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Blocked), "после паузы — тот же блок");

        await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await gateway.DisconnectAsync(TestContext.Current.CancellationToken);

        (gateway.Status.StrictBlock, gateway.Status.IsPaused, gateway.Status.Protection).ShouldBe((true, false, ProtectionChecks.For(ConnectionState.Blocked)),
            "«Закрыть сеть» через отключение тоже заканчивает паузу");

        await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        gateway.Status.IsPaused.ShouldBeFalse("подключение со строгой паузы паузу заканчивает");
        time.Advance(TimeSpan.FromMinutes(5));
        await connect;

        (gateway.Status.State, gateway.Status.IsPaused).ShouldBe((ConnectionState.Connected, false), "таймер прежней паузы подключение не трогает");
    }

    [Fact]
    public async Task Pause_in_a_block_where_attempts_ran_out_ends_with_a_new_connect_like_the_service()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);
        var connect = gateway.ConnectAsync(Server with { LatencyMs = null }, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        gateway.Status.State.ShouldBe(ConnectionState.Blocked);

        (await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        time.Advance(TimeSpan.FromMinutes(5));

        (gateway.Status.State, gateway.Status.IsPaused).ShouldBe((ConnectionState.Connecting, false), "вошли в Wi-Fi — сервер снова пробуется");
    }

    [Fact]
    public async Task Pause_while_connecting_stops_the_attempt_and_connects_after_like_the_service()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);
        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);

        (await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;

        (gateway.Status.State, gateway.Status.IsPaused, gateway.Status.Protection).ShouldBe((ConnectionState.Connecting, true, ProtectionChecks.Paused),
            "сервер за порталом не ответит — попытка встала до конца паузы");

        var connected = new TaskCompletionSource();
        gateway.StatusChanged += (_, s) =>
        {
            if (s.State == ConnectionState.Connected)
            {
                connected.TrySetResult();
            }
        };
        await gateway.ResumeProtectionAsync(TestContext.Current.CancellationToken);
        (gateway.Status.State, gateway.Status.IsPaused).ShouldBe((ConnectionState.Connecting, false), "вошли в Wi-Fi — подключение заново");
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Connect_while_connecting_switches_the_server_without_a_jump_into_itself()
    {
        var time = new FakeTimeProvider();
        using var gateway = new SimulatedGatewayClient(time);
        var first = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);

        var second = gateway.ConnectAsync(Server with { Id = "de", Host = "203.0.113.25" }, TestContext.Current.CancellationToken);
        await first;
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await second;

        (gateway.Status.State, gateway.Status.ServerId).ShouldBe((ConnectionState.Connected, "de"));
    }

    [Fact]
    public async Task Pause_is_not_for_off()
    {
        using var gateway = new SimulatedGatewayClient(new FakeTimeProvider());

        (await gateway.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeFalse();
        gateway.Status.State.ShouldBe(ConnectionState.Off);
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
            "connect  server «Нидерланды 01»  mode=vpn",
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

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _saved = AppSettings.Default;

        public AppSettings Load() => _saved;

        public void Save(AppSettings settings) => _saved = settings;
    }
}
