using Grpc.Core;
using Grpc.Net.Client;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Поздний ответ отдельного запуска фильтра не оставляет сеть прошлого аккаунта измененной.</summary>
public sealed class LocalSiteFilterAccountRaceTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    public async Task A_start_arriving_after_account_disconnect_is_stopped_even_if_the_window_cancelled(
        bool signedOut, bool resetPending, bool cancelWindow)
    {
        var settings = FilterSettings();
        var calls = new DelayedFilterCalls();
        using var window = new CancellationTokenSource();
        using var client = Client(calls, () => settings);
        var start = client.StartSiteFilterAsync(window.Token);
        await calls.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        settings = settings with { AccountSignedOut = signedOut, AccountNetworkResetPending = resetPending };
        await client.DropProtectionAsync(TestContext.Current.CancellationToken);
        calls.Disconnects.ShouldBe(1);
        if (cancelWindow)
        {
            calls.AfterStart = window.Cancel;
        }
        calls.DeliverStart.TrySetResult();

        (await start).ShouldBeFalse("поздний принятый запуск уже не принадлежит доступному аккаунту");
        calls.Disconnects.ShouldBe(2, "сначала выход снял защиту, затем пришедший позже Start потребовал повторного снятия");
        (calls.Actual.State, calls.Actual.LocalSiteFilter, client.Status.State).ShouldBe((ConnectionState.Off, false, ConnectionState.Off));
        calls.DisconnectRequests.ShouldAllBe(request => !request.KeepBlock);
        calls.DisconnectDeadlines.ShouldAllBe(deadline => deadline.HasValue);
        calls.StatusHeaders.ShouldAllBe(headers => headers != null && headers.Any(header =>
            header.Key == GatewayCapabilities.Header && header.Value == GatewayCapabilities.LocalSiteFilter));
    }

    [Fact]
    public async Task A_late_start_does_not_hide_a_failed_account_network_restore()
    {
        var settings = FilterSettings();
        var calls = new DelayedFilterCalls();
        using var client = Client(calls, () => settings);
        var start = client.StartSiteFilterAsync(TestContext.Current.CancellationToken);
        await calls.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        settings = settings with { AccountSignedOut = true };
        await client.DropProtectionAsync(TestContext.Current.CancellationToken);
        calls.NetworkRestored = false;
        calls.DeliverStart.TrySetResult();

        await Should.ThrowAsync<IOException>(() => start);
        calls.Disconnects.ShouldBe(2);
    }

    private static AppSettings FilterSettings() => AppSettings.Default with
    {
        Routing = RoutingSettings.Default with { Rules = [new DomainRule("blocked.example.com", RuleAction.Block)] },
    };

    private static ServiceGatewayClient Client(DelayedFilterCalls calls, Func<AppSettings> settings) => new(
        GrpcChannel.ForAddress("http://127.0.0.1"), ServiceKind.Installed, _ => null, settings, TimeProvider.System, calls: calls);

    private sealed class DelayedFilterCalls : CallInvoker
    {
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeliverStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConnectionStatus Actual { get; private set; } = ConnectionStatus.Initial(DateTimeOffset.UtcNow);
        public List<V1.DisconnectRequest> DisconnectRequests { get; } = [];
        public List<DateTime?> DisconnectDeadlines { get; } = [];
        public List<Metadata?> StatusHeaders { get; } = [];
        public int Disconnects => DisconnectRequests.Count;
        public Action? AfterStart { get; set; }
        public bool NetworkRestored { get; set; } = true;

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request)
        {
            async Task<TResponse> Respond()
            {
                object reply;
                if (method.Name == "StartSiteFilter")
                {
                    StartEntered.TrySetResult();
                    await DeliverStart.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                    Actual = Actual with { State = ConnectionState.Connected, Mode = ConnectionMode.Vpn, LocalSiteFilter = true };
                    AfterStart?.Invoke();
                    reply = new V1.ConnectReply { Accepted = true };
                }
                else if (method.Name == "Disconnect")
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    DisconnectRequests.Add((V1.DisconnectRequest)(object)request!);
                    DisconnectDeadlines.Add(options.Deadline);
                    if (NetworkRestored && Actual.State != ConnectionState.Off)
                    {
                        Actual = Actual.MoveTo(ConnectionState.Off, DateTimeOffset.UtcNow);
                    }
                    reply = new V1.DisconnectReply { NetworkRestored = NetworkRestored };
                }
                else if (method.Name == "GetStatus")
                {
                    StatusHeaders.Add(options.Headers);
                    if (options.CancellationToken.IsCancellationRequested)
                    {
                        throw new RpcException(new Status(StatusCode.Cancelled, "окно отменило ожидание"));
                    }
                    reply = Actual.ToContract();
                }
                else
                {
                    throw new NotSupportedException(method.Name);
                }
                return (TResponse)reply;
            }
            return new AsyncUnaryCall<TResponse>(Respond(), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => new(new WaitingReader<TResponse>(), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class WaitingReader<T> : IAsyncStreamReader<T>
    {
        public T Current => default!;
        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
    }
}
