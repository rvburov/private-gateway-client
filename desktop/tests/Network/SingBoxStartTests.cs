using System.Collections.Concurrent;
using System.Net;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// sing-box, который не поднялся. У пользователя на Windows (2026-10-30) журнал говорил только «sing-box не поднялся за 8 с»:
/// строки его запуска — уровня INFO — ниже уровня журнала по умолчанию и не писались. Теперь при отказе — последние строки.
/// Здесь sing-box — сценарий sh, который пишет строку запуска и молчит; Xray — настоящий, TUN не создается.
/// </summary>
public sealed class SingBoxStartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Silent_sing_box_leaves_its_startup_lines_in_the_journal()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sing-box здесь — сценарий sh");
        var directory = Directory.CreateTempSubdirectory("pg-silent-");
        try
        {
            var fake = Path.Combine(directory.FullName, "sing-box");
            await File.WriteAllTextAsync(fake,
                "#!/bin/sh\ncat > /dev/null &\necho 'INFO inbound/tun[tun-in]: creating adapter'\necho 'DEBUG adapter: waiting for driver' >&2\nexec sleep 120\n", Ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var (definition, _) = XrayKit.Pair("vless", XrayKit.FreePort());
            var request = new CoreRequest(definition, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);
            var journal = new ConcurrentQueue<(string Source, string Line)>();
            var started = DateTime.UtcNow;

            var error = await Should.ThrowAsync<TunnelException>(() => VpnTunnel.StartAsync(XrayKit.Xray!, fake, request, [IPAddress.Loopback],
                TimeSpan.FromSeconds(5), TimeProvider.System, output: (source, line) => journal.Enqueue((source, line)), cancellationToken: Ct));

            error.Message.ShouldBe($"sing-box не поднялся за {VpnTunnel.SingBoxStart.TotalSeconds:0} с");
            (DateTime.UtcNow - started).ShouldBeGreaterThanOrEqualTo(VpnTunnel.SingBoxStart - TimeSpan.FromSeconds(1),
                "Wintun на Windows создается не мгновенно: sing-box ждут не меньше SingBoxStart, даже если срок подключения вышел");
            journal.ShouldContain(("sing-box", "INFO inbound/tun[tun-in]: creating adapter"), "по строкам запуска видно, где sing-box застрял");
            journal.ShouldContain(("sing-box", "DEBUG adapter: waiting for driver"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
