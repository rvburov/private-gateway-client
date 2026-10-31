using System.Diagnostics;
using System.Net;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Перезапуск TUN на Windows (решение 510). У пользователя каждое подключение VPN начиналось с выхода sing-box через 15 с —
/// «create adapter: Cannot create a file when that file already exists»: служба снимала sing-box, и адаптер Wintun с тем же
/// GUID оставался. Теперь служба останавливает sing-box мягко (Ctrl+C в его консоль — он закрывает адаптер сам) и до запуска
/// убирает адаптер, оставшийся от снятого. Ядру Xray здесь быть не нужно: sing-box поднимает TUN и без него.
/// </summary>
public sealed partial class WindowsTunnelTests
{
    /// <summary>Новый адаптер — быстрее, чем Wintun ждет оставшийся (15 с).</summary>
    private static readonly TimeSpan QuickStart = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Sing_box_stops_gracefully_and_starts_again_at_once_with_the_same_adapter()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        var output = new CoreOutput();
        for (var round = 1; round <= 2; round++)
        {
            using var singBox = await StartTunAsync(output, $"запуск {round}");
            Guid.Parse(Adapter()!.Id).ShouldBe(WintunAdapter.AdapterGuid(SingBoxConfigBuilder.InterfaceName),
                "GUID адаптера — как его выводит sing-tun из имени: его устройство служба и убирает");
            await singBox.StopAsync(VpnTunnel.StopGrace, CancellationToken.None);
            singBox.ExitCode.ShouldBe(0, "sing-box вышел сам по Ctrl+C, а не снят (снятый — код -1)\n" + Text(output));
            Text(output).ShouldNotContain("FATAL");
        }

        WintunAdapter.RemoveLeftover(SingBoxConfigBuilder.InterfaceName).ShouldBeFalse("мягко остановленный sing-box убрал адаптер сам");
    }

    [Fact]
    public async Task Leftover_adapter_of_a_killed_sing_box_is_removed_before_the_next_start()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");
        var output = new CoreOutput();
        using (var killed = await StartTunAsync(output, "снятый sing-box"))
        {
            // Как раньше на Windows всегда: без просьбы, сразу снять.
            await killed.StopAsync(cancellationToken: CancellationToken.None);
            killed.ExitCode.ShouldNotBe(0);
        }

        output.Add("test", "оставшийся адаптер убран: " + WintunAdapter.RemoveLeftover(SingBoxConfigBuilder.InterfaceName));
        WintunAdapter.RemoveLeftover(SingBoxConfigBuilder.InterfaceName).ShouldBeFalse("убранного больше нет");
        using var next = await StartTunAsync(output, "запуск после снятого");
        Text(output).ShouldNotContain("FATAL");
        await next.StopAsync(VpnTunnel.StopGrace, CancellationToken.None);
    }

    /// <summary>sing-box с TUN теста; поднялся с адаптером не дольше <see cref="QuickStart"/> — иначе отказ с выводом ядра.</summary>
    private static async Task<CoreProcess> StartTunAsync(CoreOutput output, string what)
    {
        var subnet = TunSubnet.Choose(VpnTunnel.MachineNetworks());
        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(VpnTunnel.NewLink(IPAddress.Loopback), [IPAddress.Loopback], ProtectionSettings.Default,
            ConnectionSettings.Default with { CoreLog = CoreLogLevel.Debug }, new TunIpv6(Kernel: true, Tunnel: false), Routes: Routes, Subnet: subnet));
        var started = 0;
        var clock = Stopwatch.StartNew();
        var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, output: line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started, 1);
            }

            output.Add("sing-box", line);
        });
        try
        {
            await WaitAsync(() => (Volatile.Read(ref started) == 1 && Adapter() is not null) || singBox.HasExited, what, output);
            singBox.HasExited.ShouldBeFalse($"{what}: sing-box вышел при запуске\n{Text(output)}");
            clock.Elapsed.ShouldBeLessThan(QuickStart, $"{what}: адаптер поднимался {clock.Elapsed.TotalSeconds:0.0} с\n{Text(output)}");
            return singBox;
        }
        catch
        {
            singBox.Dispose();
            throw;
        }
    }

    private static string Text(CoreOutput output) => string.Join('\n', output.Lines);
}
