using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class SessionSubscriptionDownloaderTests
{
    private static readonly Uri Url = new("https://example.org/sub/fake-token");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Temporary_failure_retries_same_core_without_direct_fallback()
    {
        var direct = new Direct();
        var gateway = new Gateway();
        gateway.Download = _ => Task.FromResult(++gateway.Calls < 3
            ? SubscriptionDownload.Failed(DownloadFailure.Status, 503, TimeSpan.Zero)
            : new SubscriptionDownload("trojan://fake@192.0.2.1:443"u8.ToArray()));
        var result = await new SessionSubscriptionDownloader(direct, gateway, TimeProvider.System).DownloadAsync(Url, Ct);
        result.Text.ShouldStartWith("trojan:");
        gateway.Calls.ShouldBe(3);
        direct.Calls.ShouldBe(0);
        gateway.Attempts.ShouldBe([DownloadFailure.Status, DownloadFailure.Status, null]);
    }

    [Theory]
    [InlineData(DownloadFailure.Tls, null)]
    [InlineData(DownloadFailure.Status, 403)]
    [InlineData(DownloadFailure.TooLarge, null)]
    public async Task Permanent_failure_is_not_retried(DownloadFailure failure, int? status)
    {
        var gateway = new Gateway();
        gateway.Download = _ => { gateway.Calls++; return Task.FromResult(SubscriptionDownload.Failed(failure, status)); };
        var error = await Should.ThrowAsync<SubscriptionDownloadException>(() => new SessionSubscriptionDownloader(new Direct(), gateway, TimeProvider.System).DownloadAsync(Url, Ct));
        error.Failure.ShouldBe(failure);
        gateway.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Retry_after_is_respected_and_session_change_cancels_wait()
    {
        var time = new FakeTimeProvider();
        var direct = new Direct();
        var gateway = new Gateway();
        gateway.Download = _ => { gateway.Calls++; return Task.FromResult(SubscriptionDownload.Failed(DownloadFailure.Status, 429, TimeSpan.FromSeconds(10))); };
        var pending = new SessionSubscriptionDownloader(direct, gateway, time).DownloadAsync(Url, Ct);
        gateway.Calls.ShouldBe(1);
        time.Advance(TimeSpan.FromSeconds(9));
        gateway.Calls.ShouldBe(1);
        gateway.Change(ConnectionState.Off);
        (await Should.ThrowAsync<SubscriptionDownloadException>(() => pending)).Failure.ShouldBe(DownloadFailure.Connection);
        gateway.Calls.ShouldBe(1);
        direct.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Excessive_retry_after_does_not_retry_early()
    {
        var gateway = new Gateway();
        gateway.Download = _ => { gateway.Calls++; return Task.FromResult(SubscriptionDownload.Failed(DownloadFailure.Status, 429, TimeSpan.FromHours(1))); };
        await Should.ThrowAsync<SubscriptionDownloadException>(() => new SessionSubscriptionDownloader(new Direct(), gateway, TimeProvider.System).DownloadAsync(Url, Ct));
        gateway.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Protection_block_never_uses_direct_transport()
    {
        var direct = new Direct();
        var gateway = new Gateway();
        gateway.Change(ConnectionState.Blocked);
        await Should.ThrowAsync<SubscriptionDownloadException>(() => new SessionSubscriptionDownloader(direct, gateway, TimeProvider.System).DownloadAsync(Url, Ct));
        direct.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Stale_success_is_rejected_after_disconnect()
    {
        var gateway = new Gateway();
        gateway.Download = _ => { gateway.Change(ConnectionState.Off); return Task.FromResult(new SubscriptionDownload("secret"u8.ToArray())); };
        var direct = new Direct();
        await Should.ThrowAsync<SubscriptionDownloadException>(() => new SessionSubscriptionDownloader(direct, gateway, TimeProvider.System).DownloadAsync(Url, Ct));
        direct.Calls.ShouldBe(0);
    }

    private sealed class Direct : ISubscriptionDownloader
    {
        public int Calls { get; private set; }
        public Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SubscriptionContent("direct", null));
        }
    }

    private sealed class Gateway : IGatewayClient
    {
        public int Calls { get; set; }
        public List<DownloadFailure?> Attempts { get; } = [];
        public Func<CancellationToken, Task<SubscriptionDownload>> Download { get; set; } = _ => Task.FromResult(SubscriptionDownload.Failed(DownloadFailure.Connection));
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with { State = ConnectionState.Connected, Mode = ConnectionMode.Proxy };
        public ServiceKind Service => ServiceKind.User;
        public IReadOnlyList<JournalEntry> Journal => [];
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }
        public void Change(ConnectionState state) { Status = Status with { State = state }; StatusChanged?.Invoke(this, Status); }
        public void ReportSubscriptionAttempt(DownloadFailure? failure, int? statusCode, int attempt, TimeSpan elapsed, bool throughCore) => Attempts.Add(failure);
        public Task<SubscriptionDownload> DownloadSubscriptionAsync(Uri address, CancellationToken cancellationToken = default) => Download(cancellationToken);
        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));
        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(new Dictionary<string, LatencyResult>());
    }
}
