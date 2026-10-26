using System.Diagnostics;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Network.SingBox;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Клиент службы в интерфейсе (<see cref="ServiceGatewayClient"/>) против настоящей службы по IPC:
/// подключение и поток состояния, журнал, отказ службы, пропавшая служба, задержка, поиск службы.
/// </summary>
public sealed class ServiceGatewayClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Window_connects_through_the_service_and_follows_its_state_and_journal()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        await using var service = await RunningService.StartAsync();
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        using var client = Client(service.Endpoint, definition, Settings(XrayKit.FreePort()));
        var states = new List<ConnectionState>();
        client.StatusChanged += (_, s) => states.Add(s.State);

        await client.ConnectAsync(Profile(definition), Ct);
        await WaitFor(() => client.Status.State == ConnectionState.Connected);

        (client.Status.ServerId, client.Status.Mode).ShouldBe(("srv-1", ConnectionMode.Proxy));
        await WaitFor(() => client.Journal.Any(e => e.Source == "service" && e.Message.StartsWith("Подключено", StringComparison.Ordinal)));
        client.Journal.ShouldAllBe(e => !e.Message.Contains(XrayKit.Uuid, StringComparison.OrdinalIgnoreCase));

        await client.DisconnectAsync(Ct);
        await WaitFor(() => client.Status.State == ConnectionState.Off);
        states.SkipWhile(s => s == ConnectionState.Off).ShouldBe([ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Off],
            "состояние приходит от службы, по одному разу");
    }

    [Theory]
    [InlineData(ConnectionMode.Vpn, true, ErrorCodes.AdminRequired)]
    [InlineData(ConnectionMode.Proxy, false, ErrorCodes.BadRequest)]
    public async Task Refusal_is_the_service_error_with_its_code(ConnectionMode mode, bool known, string code)
    {
        await using var service = await RunningService.StartAsync();
        var (definition, _) = XrayKit.Pair("vless", 443);
        using var client = Client(service.Endpoint, known ? definition : null, Settings(10808) with { Mode = mode });

        await client.ConnectAsync(Profile(definition), Ct);

        await WaitFor(() => client.Status.State == ConnectionState.Error);
        client.Status.ErrorCode.ShouldBe(code, known ? "служба без прав — VPN не будет" : "сервер удален — служба отказывает");
    }

    [Fact]
    public async Task Other_vpn_stops_vpn_mode_until_the_user_goes_on_and_the_choice_ends_with_disconnect()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var routes = new FakeRoutes { Routes = [new DefaultRoute("eth0", IsTunnel: false), new DefaultRoute("wg0", IsTunnel: true)] };

        // Блок и туннель — подделки и не нужны: имя сервера не найдется, подключение остановится на поиске адреса.
        await using var service = await RunningService.StartAsync(options: o => o with
        {
            CanVpn = true, SingBoxPath = XrayKit.Xray, KillSwitch = new NoKillSwitch(), Routes = routes,
        });
        var (definition, _) = XrayKit.Pair("vless", 443);
        definition = definition with { Host = "server.invalid" };
        using var client = Client(service.Endpoint, definition, Settings(XrayKit.FreePort()) with { Mode = ConnectionMode.Vpn });

        await client.ConnectAsync(Profile(definition), Ct);

        (client.Status.State, client.Status.ErrorCode).ShouldBe((ConnectionState.Error, ErrorCodes.OtherVpn), "отказ виден сразу: окно решает по нему");
        (await client.RunChecksAsync(Ct))[DiagnosticCheck.OtherVpn]
            .ShouldBe(new DiagnosticResult(DiagnosticCheck.OtherVpn, CheckState.Warning, "vpn_adapter"));

        await client.ConnectDespiteOtherVpnAsync(Profile(definition), Ct);

        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServerNotFound, "другой VPN пропущен — подключение дошло до адреса сервера");
        service.Journal.Tail(50).ShouldContain(e => e.Message == "Работает другой VPN (wg0) — подключение по выбору пользователя");

        await client.ConnectAsync(Profile(definition), Ct);

        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServerNotFound, "выбор держится и при переподключении");

        await client.DisconnectAsync(Ct);
        await client.ConnectAsync(Profile(definition), Ct);

        client.Status.ErrorCode.ShouldBe(ErrorCodes.OtherVpn, "защиту выключили — другой VPN проверяется снова");

        if (SingBoxConfigBuilder.InterfaceName is { Length: > 0 } own)
        {
            routes.Routes = [new DefaultRoute("eth0", IsTunnel: false), new DefaultRoute(own, IsTunnel: true)];
            (await client.RunChecksAsync(Ct))[DiagnosticCheck.OtherVpn]!.State.ShouldBe(CheckState.Ok, "свой туннель — не чужой VPN");
        }
    }

    [Fact]
    public async Task Without_the_service_connect_is_an_error_and_nothing_is_measured()
    {
        var (definition, _) = XrayKit.Pair("vless", 443);
        using var client = Client(MissingEndpoint(), definition, Settings(10808));

        await client.ConnectAsync(Profile(definition), Ct);

        (client.Status.State, client.Status.ErrorCode).ShouldBe((ConnectionState.Error, ErrorCodes.ServiceNotRunning));
        client.Status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off), "без службы окно защиту не обещает");
        (await client.MeasureLatencyAsync(["srv-1"], Ct)).ShouldBeEmpty();
        (await client.RunChecksAsync(Ct)).Results.ShouldHaveSingleItem()
            .ShouldBe(new DiagnosticResult(DiagnosticCheck.Service, CheckState.Stopped, "no_reply"));
    }

    [Fact]
    public async Task Stopped_service_turns_protection_into_an_error()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var service = await RunningService.StartAsync();
        await using var _ = service;
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        using var client = Client(service.Endpoint, definition, Settings(XrayKit.FreePort()));
        await client.ConnectAsync(Profile(definition), Ct);
        await WaitFor(() => client.Status.State == ConnectionState.Connected);

        var stopping = service.DisposeAsync().AsTask();

        // Служба закрывает потоки сразу, а не по истечении 30 с на остановку.
        await WaitFor(() => client.Status.State == ConnectionState.Error);
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning, "защита без службы не подтверждена");
        client.Status.Protection.ShouldBe(ProtectionChecks.For(ConnectionState.Off));
        await stopping;
    }

    [Fact]
    public async Task Returned_service_state_replaces_the_error()
    {
        var service = await RunningService.StartAsync();
        await using var _ = service;
        var (definition, _) = XrayKit.Pair("vless", 443);
        using var client = Client(service.Endpoint, definition, Settings(10808) with { Mode = ConnectionMode.Vpn });
        await client.ConnectAsync(Profile(definition), Ct);
        await WaitFor(() => client.Status.State == ConnectionState.Error);
        await WaitFor(() => client.Journal.Any(e => e.Message.Contains("отклонено", StringComparison.Ordinal)));

        await service.DisposeAsync();
        await using var restarted = await RunningService.StartAsync(endpoint: service.Endpoint);
        restarted.Journal.Add(JournalLevel.Info, "service", "служба запущена заново");

        await WaitFor(() => client.Status.State == ConnectionState.Off);
        client.Status.ErrorCode.ShouldBeNull("окно снова слушает службу и берет ее состояние");
        await WaitFor(() => client.Journal.Any(e => e.Message == "служба запущена заново"));
        client.Journal.ShouldNotContain(e => e.Message.Contains("отклонено", StringComparison.Ordinal),
            "журнал — это журнал службы: после переподключения хвост приходит заново, без старых и повторов");
    }

    [Fact]
    public async Task Window_keeps_only_the_latest_service_journal_entries()
    {
        await using var service = await RunningService.StartAsync();
        using var client = Client(service.Endpoint, null, Settings(10808));
        var total = ServiceGatewayClient.JournalLimit + 50;
        service.Journal.Add(JournalLevel.Info, "test", "окно слушает");
        await WaitFor(() => client.Journal.Any(e => e.Message == "окно слушает"));

        // Записи приходят по одной, а не хвостом журнала: лимит держит само окно.
        for (var i = 0; i < total; i++)
        {
            service.Journal.Add(JournalLevel.Info, "test", "запись " + i);
        }

        await WaitFor(() => client.Journal is { Count: > 0 } journal && journal[^1].Message == "запись " + (total - 1));
        client.Journal.Count.ShouldBe(ServiceGatewayClient.JournalLimit);
        client.Journal[0].Message.ShouldBe("запись 50", "старые записи уходят первыми");
    }

    [Fact]
    public async Task Latency_of_own_servers_comes_through_the_service()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("204");
        await using var service = await RunningService.StartAsync(latencyTarget: site.Url);
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        using var client = Client(service.Endpoint, definition, Settings(10808));

        var results = await client.MeasureLatencyAsync(["srv-1", "deleted"], Ct);

        results.Keys.ShouldBe(["srv-1"], "сервера без описания в запросе нет");
        results["srv-1"].Milliseconds.ShouldNotBeNull();
    }

    [Fact]
    public async Task Service_is_found_at_the_first_address_that_answers()
    {
        await using var service = await RunningService.StartAsync();
        await using var other = await RunningService.StartAsync();
        var missing = new ServiceAddress(MissingEndpoint(), System: false);
        var running = new ServiceAddress(service.Endpoint, System: false);
        var second = new ServiceAddress(other.Endpoint, System: false);

        Locator([missing, running]).Find().ShouldBe(running);
        Locator([running, second]).Find().ShouldBe(running);
        Locator([missing]).Find().ShouldBeNull();
        ServiceLocator.Candidates.ShouldBe([new(IpcEndpoints.System, System: true), new(IpcEndpoints.User, System: false)],
            "сначала установленная служба — у нее есть режим VPN");
    }

    [Fact]
    public async Task Service_of_the_user_is_not_taken_for_the_installed_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "на Windows владельца pipe проверяет CheckOwner; процесс CI — администратор");
        Assert.SkipWhen(PeerCredentials.CurrentUid() == 0, "тест запущен от root — его сокету доверяют всегда");
        await using var service = await RunningService.StartAsync();

        IpcChannel.Answers(service.Endpoint, system: false, ServiceLocator.ProbeTimeout).ShouldBeTrue();
        IpcChannel.Answers(service.Endpoint, system: true, ServiceLocator.ProbeTimeout)
            .ShouldBeFalse("сокет установленной службы держит только root");
    }

    [Theory]
    [InlineData(0u, true, true)]
    [InlineData(0u, false, true)]
    [InlineData(1000u, false, true)]
    [InlineData(1000u, true, false)]
    [InlineData(1001u, false, false)]
    [InlineData(null, false, false)]
    public void Socket_of_root_or_the_user_himself_is_trusted(uint? peer, bool system, bool trusted)
    {
        IpcChannel.TrustsUnix(peer, system, self: 1000).ShouldBe(trusted);
    }

    [Fact]
    public async Task Window_starts_the_user_service_for_proxy_mode_and_it_stops_with_the_window()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        // «Окно» — отдельный процесс службы на своем адресе: его можно закрыть, не закрывая тест.
        // Своя папка состояния: служба пользователя не смотрит в снимок системного прокси этой машины.
        var state = Path.GetDirectoryName(MissingEndpoint())!;
        using var window = Process.Start(new ProcessStartInfo(ServiceExecutable) { ArgumentList = { "--user", "--ipc", MissingEndpoint(), "--state", state } })!;
        var user = new ServiceAddress(MissingEndpoint(), System: false);
        try
        {
            var settings = Settings(XrayKit.FreePort());
            var locator = new ServiceLocator([new(MissingEndpoint(), System: true), user], Answers,
                new UserServiceLauncher(ServiceExecutable, window.Id, user.Endpoint, state), TimeProvider.System);
            using var host = new GatewayHost(locator, a => Client(a.Endpoint, definition, settings), () => settings, TimeProvider.System);

            await host.ConnectAsync(Profile(definition), Ct);
            // Служба подключает: «Защищено» — когда сервер теста ответит проверочным адресом из интернета; без интернета у машины
            // теста — «Переподключение». Здесь проверяется запуск и остановка службы пользователя, а не сервер.
            await WaitFor(() => host.Status.State is ConnectionState.Connected or ConnectionState.Reconnecting);

            host.Status.Mode.ShouldBe(ConnectionMode.Proxy);
            host.Journal.ShouldContain(e => e.Source == "app" && e.Message.StartsWith("Запущена служба пользователя", StringComparison.Ordinal));
        }
        finally
        {
            // Закрыть «окно» — и служба пользователя остановится сама.
            window.Kill();
        }

        await WaitFor(() => !Answers(user));
    }

    private static ServiceGatewayClient Client(string endpoint, ServerDefinition? definition, AppSettings settings) =>
        new(IpcChannel.Create(endpoint, system: false), ServiceKind.User, id => id == "srv-1" ? definition : null, () => settings, TimeProvider.System);

    // Системный прокси выключен: тесты не трогают настройки машины.
    private static AppSettings Settings(int proxyPort) => AppSettings.Default with
    {
        Mode = ConnectionMode.Proxy,
        Protection = ProtectionSettings.Default with { AllowLan = true },
        Connection = ConnectionSettings.Default with { ProxyPort = proxyPort, ConnectTimeoutSeconds = 10, SystemProxy = false },
    };

    private static ServerProfile Profile(ServerDefinition server) =>
        new("srv-1", server.Name, "", server.Protocol, server.Security.Kind, server.Host, server.Port, "tcp", null);

    private static bool Answers(ServiceAddress address) => IpcChannel.Answers(address.Endpoint, address.System, ServiceLocator.ProbeTimeout);

    private static ServiceLocator Locator(IReadOnlyList<ServiceAddress> candidates) =>
        new(candidates, Answers, new UserServiceLauncher(null, Environment.ProcessId), TimeProvider.System);

    /// <summary>Собранная служба из соседней папки сборки — той же конфигурации, что и тест.</summary>
    private static string ServiceExecutable => Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service",
        new DirectoryInfo(AppContext.BaseDirectory).Name, UserServiceLauncher.FileName);

    private static string MissingEndpoint() => OperatingSystem.IsWindows()
        ? "PrivateGateway.Missing." + Guid.NewGuid().ToString("N")
        : Path.Combine(Path.GetTempPath(), "pg-missing-" + Guid.NewGuid().ToString("N")[..8], "ipc.sock");

    /// <summary>Блок при обрыве, который ничего не ставит: тест не трогает брандмауэр машины.</summary>
    private sealed class NoKillSwitch : IKillSwitch
    {
        public string Name => "подделка";

        public bool IsEnabled => false;

        public void Enable(KillSwitchPolicy policy)
        {
        }

        public void Disable()
        {
        }
    }

    /// <summary>Ждать условия до 20 с: состояние приходит из потока службы.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (!condition())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
}
