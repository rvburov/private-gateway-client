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

        await gateway.RunChecksAsync(TestContext.Current.CancellationToken);
        gateway.Status.Protection.CheckedAt.ShouldBeNull();

        var connect = gateway.ConnectAsync(Server, TestContext.Current.CancellationToken);
        time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        time.Advance(TimeSpan.FromMinutes(5));
        await gateway.RunChecksAsync(TestContext.Current.CancellationToken);

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
}
