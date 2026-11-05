using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Ответ старого запуска службы или чтения возможностей не начинает подключение после нового выбора пользователя.</summary>
public sealed class ExternalProxyIntentTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch.AddHours(1);
    private static readonly ServerProfile Vpn = new("vpn", "VPN", string.Empty, ProxyProtocol.Vless,
        TransportSecurity.Reality, "203.0.113.10", 443, "tcp", null);
    private static readonly ServiceAddress UserService = new("external-proxy-intent-user", System: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_delayed_service_start_does_not_connect_an_old_mode_or_proxy_revision(bool changeMode, bool despiteOtherVpn)
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        using var launcher = new DelayedLauncher();
        var service = new TargetService(time);
        using var host = Host(() => settings, time, launcher, service);
        var pending = ConnectAsync(host, Selected(settings)!, despiteOtherVpn);
        await launcher.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        settings = Changed(settings, changeMode);
        launcher.Answer.TrySetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        service.Connected.ShouldBeEmpty("ожидавшая запуска цель уже заменена пользователем");
        host.Status.ErrorCode.ShouldBeNull("устаревшая попытка не создает новую ошибку");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnect_cancels_an_intent_waiting_for_the_user_service_even_while_off(bool despiteOtherVpn)
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        using var launcher = new DelayedLauncher();
        var service = new TargetService(time);
        using var host = Host(() => settings, time, launcher, service);
        var pending = ConnectAsync(host, Selected(settings)!, despiteOtherVpn);
        await launcher.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        host.Status.State.ShouldBe(ConnectionState.Off);

        await host.DisconnectAsync(Ct);
        launcher.Answer.TrySetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        service.Connected.ShouldBeEmpty("ручное отключение во время запуска отменяет поздний Connect");
        host.Status.State.ShouldBe(ConnectionState.Off);
        host.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task The_same_intent_connects_once_after_the_user_service_answers()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        using var launcher = new DelayedLauncher();
        var service = new TargetService(time);
        using var host = Host(() => settings, time, launcher, service);
        var selected = Selected(settings)!;
        var pending = host.ConnectAsync(selected, Ct);
        await launcher.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        launcher.Answer.TrySetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        service.Connected.ShouldBe([selected.Id]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task A_delayed_capability_reply_does_not_send_a_stale_connect(bool changeMode, bool despiteOtherVpn)
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        var pending = ConnectAsync(client, Selected(settings)!, despiteOtherVpn);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        settings = Changed(settings, changeMode);
        calls.Info[0].TrySetResult(Info(supported: true));
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty("не отправляется даже пустой Server: отказ службы испортил бы новую сессию");
        calls.Disconnects.ShouldBe(0);
        client.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task A_late_unsupported_reply_cannot_disconnect_or_fail_the_newer_connected_intent()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        var first = client.ConnectAsync(Selected(settings)!, Ct);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        settings = Changed(settings, changeMode: false);
        var current = Selected(settings)!;
        calls.Info[1].TrySetResult(Info(supported: true));
        await client.ConnectAsync(current, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        client.Status.State.ShouldBe(ConnectionState.Connected);
        client.Status.ServerId.ShouldBe(current.Id);

        calls.Info[0].TrySetResult(Info(supported: false));
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBe([current.Id]);
        calls.Disconnects.ShouldBe(0, "ответ старой проверки возможностей не отключает новую сессию");
        client.Status.State.ShouldBe(ConnectionState.Connected);
        client.Status.ServerId.ShouldBe(current.Id);
        client.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Disconnect_invalidates_the_capability_request_and_its_late_answer()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        var pending = client.ConnectAsync(Selected(settings)!, Ct);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await client.DisconnectAsync(Ct);
        calls.Info[0].TrySetResult(Info(supported: true));
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        client.Status.State.ShouldBe(ConnectionState.Off);
        client.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task An_unresponsive_capability_call_has_a_deadline_and_does_not_claim_an_old_service()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        var pending = client.ConnectAsync(Selected(settings)!, Ct);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        time.Advance(ServiceGatewayClient.ExternalProxyHandshakeTimeout);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        client.Status.State.ShouldBe(ConnectionState.Error);
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning, "молчание не доказывает отсутствие возможности у старой версии");
        calls.InfoTokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue("тайм-аут отменяет и сам вызов IPC");
        calls.Info[0].TrySetResult(Info(supported: true));
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_timeout_of_an_old_capability_request_does_not_fail_the_new_choice(bool changeMode)
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        var pending = client.ConnectAsync(Selected(settings)!, Ct);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        settings = Changed(settings, changeMode);
        time.Advance(ServiceGatewayClient.ExternalProxyHandshakeTimeout);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        calls.Disconnects.ShouldBe(0);
        client.Status.ErrorCode.ShouldBeNull("тайм-аут старого намерения не портит новый выбор");
        calls.Info[0].TrySetResult(Info(supported: true));
    }

    [Fact]
    public async Task A_service_that_explicitly_lacks_external_proxy_support_is_not_sent_a_connect()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        calls.Info[0].TrySetResult(Info(supported: false));
        using var client = Client(calls, () => settings, time);

        await client.ConnectAsync(Selected(settings)!, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        client.Status.State.ShouldBe(ConnectionState.Error);
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ExternalProxyUnsupported);
    }

    [Fact]
    public async Task Caller_cancellation_of_a_noncooperative_capability_read_remains_cancellation()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        using var client = Client(calls, () => settings, time);
        using var stop = new CancellationTokenSource();
        var pending = client.ConnectAsync(Selected(settings)!, stop.Token);
        await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        calls.Connected.ShouldBeEmpty();
        calls.InfoTokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue();
        client.Status.ErrorCode.ShouldBeNull("отмена не становится сообщением о неисправности или старой службе");
        calls.Info[0].TrySetResult(Info(supported: true));
    }

    [Fact]
    public async Task Replacing_the_service_client_during_its_capability_read_silently_retires_the_old_intent()
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls();
        var client = Client(calls, () => settings, time);
        Task pending;
        using (client)
        {
            pending = client.ConnectAsync(Selected(settings)!, Ct);
            await calls.InfoEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // GatewayHost делает это при переходе от службы пользователя к установленной службе.
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        calls.InfoTokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue();
        client.Status.ErrorCode.ShouldBeNull("закрытый прежний клиент не сообщает об ошибке новой службы");
        calls.Info[0].TrySetResult(Info(supported: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unsupported_service_with_unconfirmed_cleanup_has_a_deadline_and_an_unknown_stop(bool stallStatus)
    {
        var settings = ProxySettings();
        var time = new FakeTimeProvider(Started);
        var calls = new IntentCalls { StallDisconnect = !stallStatus, StallStatus = stallStatus };
        calls.Info[0].TrySetResult(Info(supported: false));
        using var client = Client(calls, () => settings, time);
        var pending = client.ConnectAsync(Selected(settings)!, Ct);
        await calls.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        time.Advance(ServiceGatewayClient.ExternalProxyHandshakeTimeout);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        calls.Connected.ShouldBeEmpty();
        calls.Disconnects.ShouldBe(1);
        client.Status.State.ShouldBe(ConnectionState.Error);
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning,
            "известная несовместимость не подтверждает завершение зависшего отключения прежней сессии");
        calls.CleanupTokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeTrue();
        calls.CompleteCleanup();
        client.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning);
    }

    private static AppSettings ProxySettings() => AppSettings.Default with
    {
        Mode = ConnectionMode.Proxy,
        ExternalProxy = new ExternalProxySettings(ExternalProxyProtocol.Socks5, "203.0.113.24", 1080, Revision: 1),
    };

    private static AppSettings Changed(AppSettings settings, bool changeMode) => changeMode
        ? settings with { Mode = ConnectionMode.Vpn }
        : settings with { ExternalProxy = settings.ExternalProxy with { Revision = settings.ExternalProxy.Revision + 1, Host = "203.0.113.25" } };

    private static ServerProfile? Selected(AppSettings settings) =>
        settings.Mode == ConnectionMode.Proxy ? settings.ExternalProxy.ToProfile() : Vpn;

    private static Task ConnectAsync(IGatewayClient gateway, ServerProfile server, bool despiteOtherVpn) => despiteOtherVpn
        ? gateway.ConnectDespiteOtherVpnAsync(server, Ct) : gateway.ConnectAsync(server, Ct);

    private static GatewayHost Host(Func<AppSettings> settings, FakeTimeProvider time, DelayedLauncher launcher, TargetService service) =>
        new(new ServiceLocator([UserService], launcher.Answers, launcher, time), _ => service, settings, time,
            selectedTarget: () => Selected(settings()));

    private static ServiceGatewayClient Client(IntentCalls calls, Func<AppSettings> settings, FakeTimeProvider time) =>
        new(GrpcChannel.ForAddress("http://127.0.0.1"), ServiceKind.Installed,
            id => Selected(settings()) is { } profile && profile.Id == id
                ? new ServerDefinition("External proxy", string.Empty, profile.Protocol, profile.Host, profile.Port, TransportOptions.Tcp,
                    SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials()) : null,
            settings, time, calls: calls, selectedTarget: () => Selected(settings()));

    private static V1.ServiceInfo Info(bool supported)
    {
        var info = new V1.ServiceInfo { ServiceVersion = "0.1.0", ApiVersion = "1" };
        if (supported)
        {
            info.Capabilities.Add(ExternalProxySettings.Capability);
        }

        return info;
    }

    private sealed class DelayedLauncher : IUserServiceLauncher, IDisposable
    {
        private int _started;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Start()
        {
            Volatile.Write(ref _started, 1);
            return true;
        }

        public void Dispose() => Answer.TrySetResult(true);

        public bool Answers(ServiceAddress address)
        {
            if (Volatile.Read(ref _started) == 0)
            {
                return false;
            }

            Waiting.TrySetResult();
            return Answer.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class TargetService(TimeProvider time) : IGatewayClient
    {
        public List<string> Connected { get; } = [];
        public ConnectionStatus Status => ConnectionStatus.Initial(time.GetUtcNow());
        public ServiceKind Service => ServiceKind.User;
        public IReadOnlyList<JournalEntry> Journal => [];
        public event EventHandler<ConnectionStatus>? StatusChanged { add { } remove { } }
        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
        {
            Connected.Add(server.Id);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticReport([], time.GetUtcNow()));
        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
    }

    /// <summary>RPC не открывает сокет; GetServiceInfo намеренно игнорирует отмену, чтобы проверить собственный предел окна.</summary>
    private sealed class IntentCalls : CallInvoker
    {
        private int _infos;
        private ConnectionStatus _actual = ConnectionStatus.Initial(Started);
        private readonly TaskCompletionSource<V1.DisconnectReply> _disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<V1.Status> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource InfoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<TaskCompletionSource<V1.ServiceInfo>> Info { get; } =
        [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public List<CancellationToken> InfoTokens { get; } = [];
        public List<CancellationToken> CleanupTokens { get; } = [];
        public List<string> Connected { get; } = [];
        public int Disconnects { get; private set; }
        public bool StallDisconnect { get; init; }
        public bool StallStatus { get; init; }

        public void CompleteCleanup()
        {
            _disconnect.TrySetResult(new V1.DisconnectReply());
            _status.TrySetResult(_actual.ToContract());
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
        {
            if (method.Name == "GetServiceInfo")
            {
                InfoTokens.Add(options.CancellationToken);
                var index = Interlocked.Increment(ref _infos) - 1;
                InfoEntered.TrySetResult();
                return Call(Info[index].Task.ContinueWith(task => (TResponse)(object)task.GetAwaiter().GetResult(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }

            if (method.Name == "Disconnect" && StallDisconnect)
            {
                Disconnects++;
                CleanupTokens.Add(options.CancellationToken);
                CleanupEntered.TrySetResult();
                return Call(_disconnect.Task.ContinueWith(task => (TResponse)(object)task.GetAwaiter().GetResult(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }

            if (method.Name == "GetStatus" && StallStatus)
            {
                CleanupTokens.Add(options.CancellationToken);
                CleanupEntered.TrySetResult();
                return Call(_status.Task.ContinueWith(task => (TResponse)(object)task.GetAwaiter().GetResult(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            }

            object reply = method.Name switch
            {
                "Connect" => Connect((V1.ConnectRequest)(object)request),
                "GetStatus" => _actual.ToContract(),
                "Disconnect" => Disconnect(),
                _ => throw new NotSupportedException(method.Name),
            };
            return Call(Task.FromResult((TResponse)reply));
        }

        private V1.ConnectReply Connect(V1.ConnectRequest request)
        {
            Connected.Add(request.Server?.Id ?? "missing-server");
            _actual = ConnectionStatus.Initial(Started.AddMinutes(1)) with
            {
                State = ConnectionState.Connected,
                Mode = request.Mode.ToCore(),
                ServerId = request.Server?.Id,
            };
            return new V1.ConnectReply { Accepted = true };
        }

        private V1.DisconnectReply Disconnect()
        {
            Disconnects++;
            _actual = ConnectionStatus.Initial(Started.AddMinutes(1));
            return new V1.DisconnectReply();
        }

        private static AsyncUnaryCall<T> Call<T>(Task<T> reply) => new(reply, Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => new(new SilentReader<TResponse>(),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class SilentReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
    }
}
