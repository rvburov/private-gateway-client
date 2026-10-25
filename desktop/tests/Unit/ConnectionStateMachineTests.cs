using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Tests.Unit;

public class ConnectionStateMachineTests
{
    public static TheoryData<ConnectionState, ConnectionState> Allowed => new()
    {
        { ConnectionState.Off, ConnectionState.Connecting },
        { ConnectionState.Connecting, ConnectionState.Connected },
        { ConnectionState.Connecting, ConnectionState.Blocked },
        { ConnectionState.Connecting, ConnectionState.Error },
        { ConnectionState.Connecting, ConnectionState.Off },
        { ConnectionState.Connected, ConnectionState.Reconnecting },
        { ConnectionState.Connected, ConnectionState.Connecting },
        { ConnectionState.Connected, ConnectionState.Blocked },
        { ConnectionState.Connected, ConnectionState.Error },
        { ConnectionState.Connected, ConnectionState.Off },
        { ConnectionState.Reconnecting, ConnectionState.Connected },
        { ConnectionState.Reconnecting, ConnectionState.Connecting },
        { ConnectionState.Reconnecting, ConnectionState.Blocked },
        { ConnectionState.Reconnecting, ConnectionState.Error },
        { ConnectionState.Reconnecting, ConnectionState.Off },
        { ConnectionState.Blocked, ConnectionState.Connecting },
        { ConnectionState.Blocked, ConnectionState.Error },
        { ConnectionState.Blocked, ConnectionState.Off },
        { ConnectionState.Error, ConnectionState.Connecting },
        { ConnectionState.Error, ConnectionState.Blocked },
        { ConnectionState.Error, ConnectionState.Off },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void Allowed_transition_moves_to_target(ConnectionState from, ConnectionState to)
    {
        ConnectionStateMachine.CanMove(from, to).ShouldBeTrue();
        ConnectionStateMachine.Move(from, to).ShouldBe(to);
    }

    [Fact]
    public void Every_transition_outside_the_table_is_forbidden()
    {
        var allowed = Allowed.Select(row => (row.Data.Item1, row.Data.Item2)).ToHashSet();
        foreach (var from in Enum.GetValues<ConnectionState>())
        {
            foreach (var to in Enum.GetValues<ConnectionState>())
            {
                ConnectionStateMachine.CanMove(from, to).ShouldBe(allowed.Contains((from, to)), $"{from} → {to}");
            }
        }
    }

    [Fact]
    public void Off_cannot_jump_to_connected_without_connecting()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConnectionStateMachine.Move(ConnectionState.Off, ConnectionState.Connected));
    }

    [Fact]
    public void Error_recovers_only_through_connecting()
    {
        ConnectionStateMachine.CanMove(ConnectionState.Error, ConnectionState.Connected).ShouldBeFalse();
        ConnectionStateMachine.CanMove(ConnectionState.Error, ConnectionState.Connecting).ShouldBeTrue();
    }

    [Fact]
    public void User_can_always_turn_protection_off()
    {
        foreach (var state in Enum.GetValues<ConnectionState>().Where(s => s != ConnectionState.Off))
        {
            ConnectionStateMachine.CanMove(state, ConnectionState.Off).ShouldBeTrue($"{state} → Off");
        }
    }

    [Fact]
    public void Any_transition_ends_the_wifi_pause_and_the_strict_block()
    {
        var paused = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
        {
            State = ConnectionState.Blocked, StrictBlock = true, CaptivePauseUntil = DateTimeOffset.UnixEpoch.AddMinutes(5),
        };

        foreach (var state in new[] { ConnectionState.Connecting, ConnectionState.Error, ConnectionState.Off })
        {
            var moved = paused.MoveTo(state, DateTimeOffset.UnixEpoch);
            (moved.IsPaused, moved.CaptivePauseUntil, moved.StrictBlock).ShouldBe((false, null, false), state.ToString());
        }
    }

    [Fact]
    public void Traffic_bypasses_tunnel_only_when_off()
    {
        foreach (var state in Enum.GetValues<ConnectionState>())
        {
            ConnectionStateMachine.TrafficBypassesTunnel(state).ShouldBe(state == ConnectionState.Off, state.ToString());
        }
    }
}
