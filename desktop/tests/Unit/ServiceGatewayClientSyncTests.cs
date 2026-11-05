using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Никаких сокетов: ответы RPC и задержанный WatchStatus задаются независимо, как два разных канала IPC.</summary>
public sealed class ServiceGatewayClientSyncTests
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UnixEpoch.AddHours(1);
    private static readonly ServerProfile Server = new("srv", "Сервер", "", ProxyProtocol.Vless,
        TransportSecurity.Reality, "203.0.113.10", 443, "tcp", null);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Connect_refreshes_the_previous_error_before_returning_even_if_watch_is_delayed(bool accepted)
    {
        var previous = Snapshot(ConnectionState.Error, Started, ErrorCodes.OtherVpn);
        var current = Snapshot(accepted ? ConnectionState.Connected : ConnectionState.Error, Started.AddSeconds(1),
            accepted ? null : ErrorCodes.ServerNotFound);
        var calls = new FakeCalls(current, accepted);
        calls.Send(previous);
        using var client = Client(calls);
        client.Status.ErrorCode.ShouldBe(ErrorCodes.OtherVpn, "поток успел сообщить старую ошибку до новой попытки");

        await client.ConnectAsync(Server, TestContext.Current.CancellationToken);

        client.Status.ShouldBe(current);
        calls.StatusReads.ShouldBe(1, "снимок читается после любого ответа Connect, а не только отказа");
    }

    [Fact]
    public async Task An_old_watch_snapshot_does_not_replace_the_newer_snapshot_read_after_connect()
    {
        var previous = Snapshot(ConnectionState.Error, Started, ErrorCodes.OtherVpn);
        var current = Snapshot(ConnectionState.Connected, Started.AddSeconds(1));
        var calls = new FakeCalls(current, accepted: true);
        calls.Send(previous);
        using var client = Client(calls);
        await client.ConnectAsync(Server, TestContext.Current.CancellationToken);
        var received = new List<ConnectionStatus>();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, status) =>
        {
            received.Add(status);
            if (status.Traffic.LatencyMs == 11)
            {
                marker.TrySetResult();
            }
        };

        calls.Send(previous);
        calls.Send(current with { Traffic = TrafficStats.Empty with { LatencyMs = 11 } });
        await marker.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        received.ShouldAllBe(s => s.State == ConnectionState.Connected, "очередь старого WatchStatus не откатывает новое состояние");
        client.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Captive_pause_resume_and_disconnect_apply_the_status_before_returning_to_the_window()
    {
        var actual = Snapshot(ConnectionState.Connected, Started) with { LocalProxyPort = 2080, UserProxyManagedByWindow = true };
        var calls = new FakeCalls(actual, accepted: true);
        calls.Send(actual);
        using var client = Client(calls);
        var proxy = new ProxyStub();
        var original = proxy.Read();
        using var controller = new UserProxyController(new SettingsService(new MemorySettings()), client, proxy, new MemoryBackup(), new FakeTimeProvider(Started));
        controller.Start();
        controller.Status.State.ShouldBe(UserProxyState.Applied);

        (await client.PauseForCaptivePortalAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken)).ShouldBeTrue();
        client.Status.IsPaused.ShouldBeTrue();
        proxy.Read().ShouldBe(original, "страница входа открывается сразу после await — прокси уже должен быть возвращен");

        await client.ResumeProtectionAsync(TestContext.Current.CancellationToken);
        client.Status.IsPaused.ShouldBeFalse();
        controller.Status.ShouldBe(new UserProxyStatus(UserProxyState.Applied, 2080));

        await client.DisconnectAsync(TestContext.Current.CancellationToken);
        client.Status.State.ShouldBe(ConnectionState.Off);
        proxy.Read().ShouldBe(original, "обычное отключение тоже не ждет задержанного WatchStatus");
    }

    [Fact]
    public async Task The_first_real_off_is_announced_even_if_it_equals_the_windows_initial_snapshot()
    {
        var initial = ConnectionStatus.Initial(Started);
        var calls = new FakeCalls(initial, accepted: true);
        using var client = Client(calls);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, _) => received.TrySetResult();
        client.HasReceivedStatus.ShouldBeFalse();

        calls.Send(initial);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        client.HasReceivedStatus.ShouldBeTrue("контроллер теперь вправе считать Off ответом службы и вернуть старый прокси");
        client.Status.ShouldBe(initial);
    }

    private static ConnectionStatus Snapshot(ConnectionState state, DateTimeOffset since, string? error = null) =>
        ConnectionStatus.Initial(since) with { State = state, Mode = ConnectionMode.Proxy, ErrorCode = error };

    private static ServiceGatewayClient Client(FakeCalls calls) => new(GrpcChannel.ForAddress("http://127.0.0.1"),
        ServiceKind.Installed, _ => null, () => AppSettings.Default with { Mode = ConnectionMode.Proxy },
        new FakeTimeProvider(Started), calls: calls);

    private sealed class FakeCalls(ConnectionStatus actual, bool accepted) : CallInvoker
    {
        private readonly Channel<V1.Status> _statuses = Channel.CreateUnbounded<V1.Status>();
        private ConnectionStatus _actual = actual;
        private readonly ConnectionStatus _connected = actual;
        public int StatusReads { get; private set; }
        public void Send(ConnectionStatus status) => _statuses.Writer.TryWrite(status.ToContract()).ShouldBeTrue();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
        {
            object reply = method.Name switch
            {
                "Connect" => new V1.ConnectReply { Accepted = accepted },
                "GetStatus" => ReadStatus(),
                "PauseForCaptivePortal" => Pause((V1.CaptivePauseRequest)(object)request),
                "Disconnect" => Disconnect(),
                _ => throw new NotSupportedException(method.Name),
            };
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)reply), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        private V1.Status ReadStatus()
        {
            StatusReads++;
            return _actual.ToContract();
        }

        private V1.CaptivePauseReply Pause(V1.CaptivePauseRequest request)
        {
            _actual = request.Cancel ? _connected : _connected with
            {
                CaptivePauseUntil = Started.AddMinutes(5),
                LocalProxyPort = null,
                UserProxyManagedByWindow = false,
            };
            return new V1.CaptivePauseReply
            {
                Until = _actual.CaptivePauseUntil is { } until ? Timestamp.FromDateTimeOffset(until) : null,
            };
        }

        private V1.DisconnectReply Disconnect()
        {
            _actual = _connected with { State = ConnectionState.Off, LocalProxyPort = null, UserProxyManagedByWindow = false };
            return new V1.DisconnectReply();
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => new(new Reader<TResponse>(method.Name == "WatchStatus" ? _statuses.Reader : null),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class Reader<T>(ChannelReader<V1.Status>? reader) : IAsyncStreamReader<T>
    {
        public T Current { get; private set; } = default!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (reader is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return false;
            }

            Current = (T)(object)await reader.ReadAsync(cancellationToken);
            return true;
        }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Load() => AppSettings.Default with { Mode = ConnectionMode.Proxy };
        public void Save(AppSettings settings) { }
    }

    private sealed class MemoryBackup : IUserProxyBackupStore
    {
        private UserProxyBackup? _snapshot;
        public UserProxyBackup? Read() => _snapshot;
        public void Write(UserProxyBackup snapshot) => _snapshot = snapshot;
        public void Delete() => _snapshot = null;
    }

    private sealed class ProxyStub : ISystemProxy
    {
        private SystemProxySnapshot _current = new(new Dictionary<string, string?> { ["ProxyEnable"] = "0" });
        public string Name => "стенд";
        public SystemProxySnapshot Read() => _current;
        public void Enable(int port) => _current = CurrentUserWindowsProxy.ForPort(port);
        public void Restore(SystemProxySnapshot previous) => _current = previous;
        public SystemProxySnapshot? Expected(int port) => CurrentUserWindowsProxy.ForPort(port);
    }
}
