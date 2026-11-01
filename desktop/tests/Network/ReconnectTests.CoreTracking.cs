using System.Diagnostics;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed partial class ReconnectTests
{
    [Fact]
    public async Task Core_tracking_and_reconnection_survive_a_journal_filled_with_warnings()
    {
        await using var rig = await ProxyRig.CreateAsync();
        using var watch = rig.Service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await rig.Service.Client.ConnectAsync(ServiceIpcTests.Request(rig.Definition, XrayKit.FreePort()), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, state => state.State == V1.ConnectionState.Connected);
        var before = CoreProcessId(rig.Service);
        for (var i = 0; i < ServiceJournal.Capacity + 1; i++)
        {
            rig.Service.Journal.Add(JournalLevel.Warning, "service", "предупреждение стенда " + i);
        }

        rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldAllBe(entry => entry.Level != JournalLevel.Info,
            "стартовые Info вытеснены предупреждениями, как в долгом прогоне");
        CoreProcessId(rig.Service).ShouldBe(before, "PID не зависит от того, сохранилось ли сообщение в Tail");
        for (var round = 0; round < 2; round++)
        {
            using var process = Process.GetProcessById(before);
            string.Equals(process.ProcessName, "xray", StringComparison.OrdinalIgnoreCase).ShouldBeTrue("снимается реальное ядро стенда");
            process.Kill();
            await ServiceIpcTests.ReadUntilAsync(watch, state => state.State == V1.ConnectionState.Reconnecting, rig.Service.Journal);
            await ServiceIpcTests.ReadUntilAsync(watch, state => state.State == V1.ConnectionState.Connected, rig.Service.Journal);
            var after = CoreProcessId(rig.Service);
            after.ShouldNotBe(before, "переподключение запустило новое ядро");
            using var replacement = Process.GetProcessById(after);
            replacement.HasExited.ShouldBeFalse("новый PID принадлежит работающему ядру");
            rig.Service.Journal.Tail(ServiceJournal.Capacity).ShouldAllBe(entry => entry.Level != JournalLevel.Info,
                "новое сообщение с PID тоже не удерживается журналом, но tracker его получил");
            before = after;
        }
    }
}
