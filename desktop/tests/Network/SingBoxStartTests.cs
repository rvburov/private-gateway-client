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

    [Fact]
    public async Task Sing_box_that_exits_at_start_gets_one_more_try()
    {
        // У пользователя на Windows (2026-10-31) первое подключение после старта службы падало, а второе проходило.
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sing-box здесь — сценарий sh");
        var directory = Directory.CreateTempSubdirectory("pg-retry-");
        try
        {
            var marker = Path.Combine(directory.FullName, "first");
            var fake = await Fake(directory, $"""
                if [ ! -e '{marker}' ]; then
                  cat > /dev/null
                  touch '{marker}'
                  echo 'FATAL start service: set ipv4 address: The object already exists'
                  exit 1
                fi
                cat > /dev/null &
                echo 'INFO sing-box started'
                exec sleep 120
                """);
            var journal = new ConcurrentQueue<(string Source, string Line)>();

            await using var tunnel = await VpnTunnel.StartAsync(XrayKit.Xray!, fake, Request(), [IPAddress.Loopback], TimeSpan.FromSeconds(5),
                TimeProvider.System, output: (source, line) => journal.Enqueue((source, line)), cancellationToken: Ct);

            journal.ShouldContain(("service", "sing-box вышел при запуске (код 1) — еще одна попытка"));
            journal.ShouldContain(("sing-box", "FATAL start service: set ipv4 address: The object already exists"), "причина первой попытки — в журнале");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Sing_box_that_exits_twice_is_a_failure_after_one_retry()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sing-box здесь — сценарий sh");
        var directory = Directory.CreateTempSubdirectory("pg-retry-");
        try
        {
            // Как настоящий sing-box: сначала дочитать конфиг, потом выйти.
            var fake = await Fake(directory, "cat > /dev/null\necho 'FATAL start service: broken'\nexit 1\n");
            var journal = new ConcurrentQueue<(string Source, string Line)>();

            var error = await Should.ThrowAsync<TunnelException>(() => VpnTunnel.StartAsync(XrayKit.Xray!, fake, Request(), [IPAddress.Loopback],
                TimeSpan.FromSeconds(5), TimeProvider.System, output: (source, line) => journal.Enqueue((source, line)), cancellationToken: Ct));

            error.Message.ShouldBe("sing-box вышел при запуске, код 1");
            journal.Count(e => e.Line.EndsWith("еще одна попытка", StringComparison.Ordinal)).ShouldBe(1, "повтор — один");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CoreRequest Request() =>
        new(XrayKit.Pair("vless", XrayKit.FreePort()).Server, ConnectionMode.Vpn, ProtectionSettings.Default, ConnectionSettings.Default, RoutingSettings.Default);

    private static async Task<string> Fake(DirectoryInfo directory, string script)
    {
        var fake = Path.Combine(directory.FullName, "sing-box");
        await File.WriteAllTextAsync(fake, "#!/bin/sh\n" + script.Replace("\r", string.Empty, StringComparison.Ordinal), Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return fake;
    }
}
