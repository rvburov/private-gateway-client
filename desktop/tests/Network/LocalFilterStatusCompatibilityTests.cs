using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service.Ipc;

namespace PrivateGateway.Client.Tests.Network;

public sealed class LocalFilterStatusCompatibilityTests
{
    [Theory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Error)]
    public void A_legacy_window_never_receives_a_protected_vpn_state_for_an_active_local_filter(ConnectionState state)
    {
        var actual = ConnectionStatus.Initial(DateTimeOffset.UtcNow) with
        {
            State = state,
            Mode = ConnectionMode.Vpn,
            LocalSiteFilter = true,
            Exit = new ExitInfo("192.0.2.10", null, null, null, "192.0.2.10"),
            Protection = new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Ok, CheckState.Off, null),
        };

        var legacy = GatewayRpc.StatusForClient(actual, supportsLocalFilter: false).ToCore();
        var capable = GatewayRpc.StatusForClient(actual, supportsLocalFilter: true).ToCore();

        (legacy.State, legacy.Mode, legacy.LocalSiteFilter, legacy.ErrorCode)
            .ShouldBe((ConnectionState.Error, ConnectionMode.Proxy, false, ErrorCodes.LocalFilterClientUnsupported));
        legacy.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off, ConnectionMode.Proxy));
        capable.ShouldBe(actual, "новое окно получает настоящий режим и его честные состояния");
        actual.LocalSiteFilter.ShouldBeTrue("представление для старого окна не останавливает сам фильтр");
    }

    [Fact]
    public void Ordinary_vpn_and_stopped_protection_keep_the_original_wire_status_for_legacy_windows()
    {
        var vpn = ConnectionStatus.Initial(DateTimeOffset.UtcNow).MoveTo(ConnectionState.Connecting, DateTimeOffset.UtcNow)
            .MoveTo(ConnectionState.Connected, DateTimeOffset.UtcNow);
        GatewayRpc.StatusForClient(vpn, supportsLocalFilter: false).ShouldBe(vpn.ToContract());
        var off = vpn.MoveTo(ConnectionState.Off, DateTimeOffset.UtcNow);
        GatewayRpc.StatusForClient(off, supportsLocalFilter: false).ShouldBe(off.ToContract());
    }
}
