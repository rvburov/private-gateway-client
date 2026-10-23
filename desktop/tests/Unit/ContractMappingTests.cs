using PrivateGateway.Client.Contracts;
using CoreProtocol = PrivateGateway.Client.Core.ProxyProtocol;
using CoreSecurity = PrivateGateway.Client.Core.TransportSecurity;
using CoreState = PrivateGateway.Client.Core.ConnectionState;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

// Новое значение в перечислении ядра или контракта без строки в ContractMapping роняет эти тесты.
public class ContractMappingTests
{
    [Fact]
    public void Every_state_round_trips()
    {
        foreach (var state in Enum.GetValues<CoreState>())
        {
            state.ToContract().ToCore().ShouldBe(state);
        }
    }

    [Fact]
    public void Every_protocol_round_trips()
    {
        foreach (var protocol in Enum.GetValues<CoreProtocol>())
        {
            protocol.ToContract().ToCore().ShouldBe(protocol);
        }
    }

    [Fact]
    public void Every_security_round_trips()
    {
        foreach (var security in Enum.GetValues<CoreSecurity>())
        {
            security.ToContract().ToCore().ShouldBe(security);
        }
    }

    [Fact]
    public void Every_contract_value_except_unspecified_maps_to_core()
    {
        foreach (var state in Enum.GetValues<V1.ConnectionState>().Where(s => s != V1.ConnectionState.Unspecified))
        {
            state.ToCore().ToContract().ShouldBe(state);
        }

        foreach (var protocol in Enum.GetValues<V1.Protocol>().Where(p => p != V1.Protocol.Unspecified))
        {
            protocol.ToCore().ToContract().ShouldBe(protocol);
        }

        foreach (var security in Enum.GetValues<V1.SecurityType>().Where(s => s != V1.SecurityType.Unspecified))
        {
            security.ToCore().ToContract().ShouldBe(security);
        }
    }

    [Fact]
    public void Unspecified_from_sender_is_rejected_not_guessed()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => V1.ConnectionState.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.Protocol.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.SecurityType.Unspecified.ToCore());
    }
}
