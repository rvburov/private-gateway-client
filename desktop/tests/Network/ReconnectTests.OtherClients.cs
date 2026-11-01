using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Другой VPN или прокси-клиент, включенный во время подключения, служба замечает своей частой проверкой (решение 516), а не
/// только с пробой сервера раз в минуту.
/// </summary>
public sealed partial class ReconnectTests
{
    [Fact]
    public async Task Another_client_core_is_noticed_by_the_frequent_check_between_server_probes()
    {
        // Проба сервера и паузы — раз в час: успеть может только частая проверка других клиентов.
        var hour = TimeSpan.FromHours(1);
        IReadOnlyList<RunningProcess> running = [];
        await using var rig = await ProxyRig.CreateAsync(new ReconnectTiming(hour, TimeSpan.FromSeconds(2), hour, hour, hour), processes: () => running,
            othersWatch: TimeSpan.FromMilliseconds(200));
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        running = [new RunningProcess(7, "xray", "/opt/other-client/xray")];
        var noted = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.OtherClients.Count > 0, rig.Service.Journal))[^1];

        noted.OtherClients.Select(c => (c.Program, c.Sign)).ShouldBe([(string.Empty, V1.OtherClientSign.Process)],
            "признак без названия программы");
    }
}
