using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class VirtualMachineProtectionTests
{
    [Theory]
    [InlineData(null, true, true, "rules_applied")]
    [InlineData(null, true, false, "rules_missing")]
    [InlineData(null, false, false, "tunnel_unverified")]
    [InlineData("hyper_v_unavailable", true, false, "unavailable")]
    [InlineData("access denied", true, false, "firewall_error")]
    public void Mechanism_state_never_promises_a_test_of_packets_from_virtual_machines(
        string? warning, bool withoutTunnel, bool rulesIntact, string reason)
    {
        var result = VirtualMachineProtection.AfterFirewall(warning, withoutTunnel, rulesIntact);

        result.ShouldBe(new VirtualMachineProtection(CheckState.Warning, reason),
            "наличие правил, поднятый TUN и отсутствие ошибки не доказывают путь пакетов всех виртуальных сетей");
    }

}
