using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Задержка через настоящее ядро: свой сервер VLESS и свой сайт на 127.0.0.1 вместо проверочного
/// адреса. Каждый сервер проверяется своим путем — живой, мертвый и неподдерживаемый не путаются.
/// </summary>
public sealed class LatencyProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Each_server_is_measured_through_itself()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var serverPort = XrayKit.FreePort();
        var (alive, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var dead = XrayKit.Definition(ProxyProtocol.Vless, XrayKit.FreePort(), new ServerCredentials(Uuid: XrayKit.Uuid));
        var hysteria = XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password)) with
        {
            Transport = TransportOptions.Quic,
        };

        var results = await LatencyProbe.MeasureAsync(
            XrayKit.Xray!, [dead, alive, hysteria, alive], ConnectionSettings.Default, site.Url, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.Select(r => r.Failure).ShouldBe([LatencyFailure.NoResponse, null, LatencyFailure.Unsupported, null]);
        results[1].Milliseconds.ShouldNotBeNull().ShouldBeInRange(1, (int)Timeout.TotalMilliseconds);
        results[3].Milliseconds.ShouldNotBeNull();
        site.Requests.ShouldBe(2, "до сайта дошли только запросы через живой сервер");
    }

    [Fact]
    public async Task Server_that_refuses_the_key_is_no_response_at_once()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        var serverPort = XrayKit.FreePort();
        var (alive, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var wrongKey = alive with { Credentials = new ServerCredentials(Uuid: "99999999-2222-3333-4444-555555555555") };
        // Таймаут запроса — минута: в замер входит и запуск ядра, а на загруженной машине CI он идет секунды.
        var timeout = TimeSpan.FromMinutes(1);
        var started = TimeProvider.System.GetTimestamp();

        var results = await LatencyProbe.MeasureAsync(
            XrayKit.Xray!, [wrongKey], ConnectionSettings.Default, site.Url, timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.ShouldBe([LatencyResult.Failed(LatencyFailure.NoResponse)]);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(timeout / 2, "обрыв, а не ожидание до таймаута");
        site.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Only_unsupported_servers_need_no_core()
    {
        var hysteria = XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password));
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-xray-" + Guid.NewGuid().ToString("N"), "xray");

        var results = await LatencyProbe.MeasureAsync(missing, [hysteria], ConnectionSettings.Default, LatencyProbe.DefaultTarget, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.ShouldBe([LatencyResult.Failed(LatencyFailure.Unsupported)]);
    }

    [Fact]
    public async Task Core_that_does_not_start_fails_the_check_not_the_servers()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var serverPort = XrayKit.FreePort();
        var (alive, _) = XrayKit.Pair("vless", serverPort);
        // Ядро с битым отпечатком TLS не примет конфиг и выйдет сразу.
        var broken = alive with { Security = new SecurityOptions(TransportSecurity.Tls, Fingerprint: "not-a-fingerprint") };

        var started = TimeProvider.System.GetTimestamp();

        var results = await LatencyProbe.MeasureAsync(
            XrayKit.Xray!, [broken], ConnectionSettings.Default, LatencyProbe.DefaultTarget, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.ShouldBe([LatencyResult.Failed(LatencyFailure.CoreFailed)]);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "ядро вышло — ждать входов незачем");
    }
}
