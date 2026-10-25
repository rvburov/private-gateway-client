using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

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
    public async Task Without_the_service_connect_is_an_error_and_nothing_is_measured()
    {
        var (definition, _) = XrayKit.Pair("vless", 443);
        using var client = Client(MissingEndpoint(), definition, Settings(10808));

        await client.ConnectAsync(Profile(definition), Ct);

        (client.Status.State, client.Status.ErrorCode).ShouldBe((ConnectionState.Error, ErrorCodes.ServiceNotRunning));
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

        ServiceLocator.Find([missing, running]).ShouldBe(running);
        ServiceLocator.Find([running, second]).ShouldBe(running);
        ServiceLocator.Find([missing]).ShouldBeNull();
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

    private static ServiceGatewayClient Client(string endpoint, ServerDefinition? definition, AppSettings settings) =>
        new(IpcChannel.Create(endpoint, system: false), id => id == "srv-1" ? definition : null, () => settings, TimeProvider.System);

    private static AppSettings Settings(int proxyPort) => AppSettings.Default with
    {
        Mode = ConnectionMode.Proxy,
        Protection = ProtectionSettings.Default with { AllowLan = true },
        Connection = ConnectionSettings.Default with { ProxyPort = proxyPort, ConnectTimeoutSeconds = 10 },
    };

    private static ServerProfile Profile(ServerDefinition server) =>
        new("srv-1", server.Name, "", server.Protocol, server.Security.Kind, server.Host, server.Port, "tcp", null);

    private static string MissingEndpoint() => OperatingSystem.IsWindows()
        ? "PrivateGateway.Missing." + Guid.NewGuid().ToString("N")
        : Path.Combine(Path.GetTempPath(), "pg-missing-" + Guid.NewGuid().ToString("N")[..8], "ipc.sock");

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
