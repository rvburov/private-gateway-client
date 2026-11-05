using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Служба подтверждает внешний путь до Connected и системного прокси; секреты остаются закрыты.</summary>
public sealed class ExternalProxyRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-external-runtime-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task A_local_listener_does_not_confirm_a_wrong_external_password(string kind)
    {
        RequireXray();
        using var site = new LocalSite("should not pass authentication");
        var externalPort = XrayKit.FreePort();
        var (definition, config) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true);
        using var upstream = await XrayKit.Start(config, externalPort);
        definition = definition with { Credentials = definition.Credentials with { Password = "wrong-test-password" } };
        var proxy = new FakeSystemProxy();
        // Периодические пробы выключены явно; для внешнего прокси начальное подтверждение все равно обязательно.
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url,
            systemProxy: (proxy, Path.Combine(_root, "system-proxy.json")));
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = Request(definition, XrayKit.FreePort());

        (await service.Client.ConnectAsync(request, cancellationToken: Ct)).Accepted.ShouldBeTrue();
        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error, service.Journal);

        states[^1].State.ShouldBe(V1.ConnectionState.Error, "локальный порт открыт, но внешняя авторизация не прошла");
        states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected);
        states[^1].Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        proxy.Port.ShouldBeNull("системный прокси не направляется в неподтвержденное подключение");
        site.Requests.ShouldBe(0);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("socks5")]
    public async Task Confirmed_external_authentication_uses_the_local_system_proxy_port_and_hides_short_secrets(string kind)
    {
        RequireXray();
        const string username = "u";
        const string password = "p";
        using var site = new LocalSite(ExternalProxyFixture.ExitPage);
        var externalPort = XrayKit.FreePort();
        var (definition, config) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true, username, password);
        using var upstream = await XrayKit.Start(config, externalPort);
        var proxy = new FakeSystemProxy();
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url,
            systemProxy: (proxy, Path.Combine(_root, "system-proxy.json")));
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var localPort = XrayKit.FreePort();

        await service.Client.ConnectAsync(Request(definition, localPort), cancellationToken: Ct);
        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error, service.Journal);

        states[^1].State.ShouldBe(V1.ConnectionState.Connected);
        site.Requests.ShouldBeGreaterThan(0, "до Connected прошла реальная сетевая проба через внешнюю авторизацию");
        (proxy.Port, service.Gateway.Status.LocalProxyPort).ShouldBe(((int?)localPort, (int?)localPort));
        proxy.Port.ShouldNotBe(externalPort, "системный прокси указывает на локальный вход приложения");
        service.Journal.Add(JournalLevel.Warning, "xray", $"credential check: {username} / {password}");
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(username + ":" + password));
        // В dTpw есть короткий пароль p: его замена раньше полного Basic-токена оставила бы узнаваемый хвост.
        service.Journal.Add(JournalLevel.Warning, "xray", $"Proxy-Authorization: Basic {basic}");
        var journal = string.Join('\n', service.Journal.Tail(ServiceJournal.Capacity).Select(line => line.Message));
        // Короткие логин и пароль скрываются только целым словом: буквы «u» и «p» в других словах журнала не трогаются.
        foreach (var secret in new[] { username, password })
            System.Text.RegularExpressions.Regex.IsMatch(journal, $@"(?<![\p{{L}}\p{{N}}_]){secret}(?![\p{{L}}\p{{N}}_])")
                .ShouldBeFalse($"короткий секрет «{secret}» остался в журнале отдельным словом");
        journal.ShouldNotContain(basic, Case.Sensitive);
        journal.ShouldContain("credential check: *** / ***");
        journal.ShouldContain("Basic ***");

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        proxy.Port.ShouldBeNull("штатное отключение возвращает прежние системные настройки");
    }

    [Fact]
    public async Task An_upstream_failure_is_reported_without_releasing_the_managed_system_proxy()
    {
        RequireXray();
        using var site = new LocalSite("external path");
        var externalPort = XrayKit.FreePort();
        var (definition, config) = ExternalProxyFixture.Pair("socks5", externalPort, authenticated: true);
        using var upstream = await XrayKit.Start(config, externalPort);
        var proxy = new FakeSystemProxy();
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url,
            systemProxy: (proxy, Path.Combine(_root, "system-proxy.json")), options: o => o with { Reconnect = ReconnectTests.Fast });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var localPort = XrayKit.FreePort();
        await service.Client.ConnectAsync(Request(definition, localPort), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected, service.Journal);
        upstream.Dispose();

        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error, service.Journal);

        states[^1].Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected);
        service.Gateway.Status.LocalProxyPort.ShouldBeNull("после ошибки служба не подтверждает готовность локального входа");
        proxy.Port.ShouldBe(localPort, "пока подключение сломано, управляемые браузеры не переключаются молча на прямой выход");
        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        proxy.Port.ShouldBeNull();
    }

    [Theory]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task A_retry_cannot_restore_an_unauthenticated_proxy_from_the_local_listener(string kind)
    {
        RequireXray();
        using var site = new LocalSite("authentication still required");
        var externalPort = XrayKit.FreePort();
        var (definition, config) = ExternalProxyFixture.Pair(kind, externalPort, authenticated: true);
        using var upstream = await XrayKit.Start(config, externalPort);
        definition = definition with { Credentials = definition.Credentials with { Password = "wrong-test-password" } };
        var timing = new ReconnectTiming(null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2));
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url, options: o => o with { Reconnect = timing });
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        var request = Request(definition, XrayKit.FreePort());
        request.Connection.Reconnect = V1.ReconnectPolicy.UpTo5Minutes;

        await service.Client.ConnectAsync(request, cancellationToken: Ct);
        var states = await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error, service.Journal);

        states.ShouldContain(s => s.State == V1.ConnectionState.Reconnecting && s.Attempt >= 1, "проверяется и повторный запуск ядра");
        states.ShouldNotContain(s => s.State == V1.ConnectionState.Connected, "Every=null не отменяет подтверждение внешнего пути при восстановлении");
        states[^1].Error.Code.ShouldBe(ErrorCodes.ServerNotResponding);
        site.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task Unsupported_udp_dns_for_http_has_a_fixed_actionable_error()
    {
        var (definition, _) = ExternalProxyFixture.Pair("http", 8080, authenticated: false);
        await using var service = await RunningService.StartAsync();
        var request = Request(definition, XrayKit.FreePort());
        request.Protection = (AppSettings.Default with
        {
            Mode = ConnectionMode.Proxy,
            Protection = ProtectionSettings.Default with { Dns = DnsChoice.Custom, CustomDns = "9.9.9.9" },
        }).ToContract();

        var reply = await service.Client.ConnectAsync(request, cancellationToken: Ct);

        reply.Accepted.ShouldBeFalse();
        reply.Error.Code.ShouldBe(ErrorCodes.ProxyDnsUnsupported);
        service.Gateway.Status.State.ShouldBe(ConnectionState.Error);
        service.Gateway.Status.ErrorCode.ShouldBe(ErrorCodes.ProxyDnsUnsupported);
        service.Journal.Tail(ServiceJournal.Capacity).ShouldContain(line => line.Message.Contains("выберите DoH", StringComparison.Ordinal));
    }

    private static V1.ConnectRequest Request(ServerDefinition definition, int localPort)
    {
        var request = ServiceIpcTests.Request(definition, localPort, systemProxy: true);
        request.Server.Id = "external-proxy:1";
        request.Connection.Reconnect = V1.ReconnectPolicy.Never;
        request.Connection.ConnectTimeout = Google.Protobuf.WellKnownTypes.Duration.FromTimeSpan(TimeSpan.FromSeconds(3));
        request.Protection.CheckLeaksAfterConnect = false;
        // В отличие от общего legacy-фикстурного helper здесь сохраняется обычный запрет LAN.
        request.Protection.AllowLan = false;
        return request;
    }

    private static void RequireXray() => Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
}
