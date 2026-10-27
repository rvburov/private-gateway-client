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

        // Сервер и сайт теста только что запущены, а у службы сервер давно работает. На Windows в CI первый проход через
        // свежий сервер бывал дольше 5 с у обоих живых сразу, а тот же замер следом — 21–26 мс (прогоны 291, 306). Прогрев — один
        // запрос через сервер со сроком в минуту; замер ниже — с ядром замера, запущенным заново, как у службы.
        var warm = await LatencyProbe.MeasureAsync(
            XrayKit.Xray!, null, [alive], ConnectionSettings.Default, site.Url, TimeSpan.FromMinutes(1), TimeProvider.System, TestContext.Current.CancellationToken);
        warm[0].Failure.ShouldBeNull("сервер теста не ответил и за минуту");
        var started = TimeProvider.System.GetTimestamp();
        var poolBefore = Pool();

        var results = await LatencyProbe.MeasureAsync(
            XrayKit.Xray!, null, [dead, alive, hysteria, alive], ConnectionSettings.Default, site.Url, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        if (results[1].Failure is not null || results[3].Failure is not null)
        {
            // Живой и прогретый сервер не ответил за срок — тогда медлит само ядро замера, и это задело бы и службу: тот же
            // замер с минутой — медленно или не отвечает вовсе.
            var took = TimeProvider.System.GetElapsedTime(started);
            // Повторы в прогонах 291 и 306 — в начале прогона, когда тесты стартуют разом: очередь пула потоков покажет,
            // не ждали ли продолжения запроса свободного потока.
            var pool = $"пул потоков до замера: {poolBefore}, после: {Pool()}";
            var again = await LatencyProbe.MeasureAsync(
                XrayKit.Xray!, null, [alive], ConnectionSettings.Default, site.Url, TimeSpan.FromMinutes(1), TimeProvider.System, TestContext.Current.CancellationToken);
            Assert.Fail($"Живой сервер не ответил за {Timeout.TotalSeconds:0} с (замер шел {took.TotalSeconds:0.0} с, {pool}): " +
                $"{string.Join(", ", results)}; тот же замер с минутой: {again[0]}");
        }

        results.Select(r => r.Failure).ShouldBe([LatencyFailure.NoResponse, null, LatencyFailure.Unsupported, null]);
        results[1].Milliseconds.ShouldNotBeNull().ShouldBeInRange(1, (int)Timeout.TotalMilliseconds);
        results[3].Milliseconds.ShouldNotBeNull();
        site.Requests.ShouldBe(3, "до сайта дошли только прогрев и запросы через живой сервер");
    }

    private static string Pool() => $"потоков {ThreadPool.ThreadCount}, в очереди {ThreadPool.PendingWorkItemCount}";

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
            XrayKit.Xray!, null, [wrongKey], ConnectionSettings.Default, site.Url, timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.ShouldBe([LatencyResult.Failed(LatencyFailure.NoResponse)]);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(timeout / 2, "обрыв, а не ожидание до таймаута");
        site.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Only_unsupported_servers_need_no_core()
    {
        var hysteria = XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password));
        var missing = Path.Combine(Path.GetTempPath(), "pg-no-xray-" + Guid.NewGuid().ToString("N"), "xray");

        var results = await LatencyProbe.MeasureAsync(missing, null, [hysteria], ConnectionSettings.Default, LatencyProbe.DefaultTarget, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

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
            XrayKit.Xray!, null, [broken], ConnectionSettings.Default, LatencyProbe.DefaultTarget, Timeout, TimeProvider.System, TestContext.Current.CancellationToken);

        results.ShouldBe([LatencyResult.Failed(LatencyFailure.CoreFailed)]);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "ядро вышло — ждать входов незачем");
    }
}
