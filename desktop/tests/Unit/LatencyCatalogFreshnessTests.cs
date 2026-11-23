using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class LatencyCatalogFreshnessTests : IDisposable
{
    private static readonly string[] TestLinks = ["trojan://not-a-real-password@198.51.100.1:443#A", "trojan://not-a-real-password@198.51.100.2:443#B"];
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-latency-" + Guid.NewGuid().ToString("N")));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
    private readonly FakeSecrets _secrets = new();

    [Fact]
    public async Task A_stable_authenticated_context_starts_one_debounced_measurement_and_counter_ticks_do_not_repeat_it()
    {
        var user = Store();
        var gateway = new StreamingGateway(automatic: true);
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway, _time);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += (_, _) =>
        {
            if (catalog.Servers[0].LatencyMs == 42 && catalog.Servers.All(server => !server.IsMeasuringLatency))
            {
                finished.TrySetResult();
            }
        };
        _time.Advance(TimeSpan.FromMilliseconds(999));
        gateway.Starts.ShouldBe(0);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gateway.Results.Writer.TryWrite(new(user.Servers[0].Id, new LatencyResult(42, null)));
        gateway.Results.Writer.Complete();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        gateway.Send(gateway.Status with { Traffic = new TrafficStats(42, 1, 2) });
        _time.Advance(TimeSpan.FromMinutes(1));

        gateway.Starts.ShouldBe(1, "завершение своей пробы и тики трафика не запускают следующий круг");
        catalog.Servers[0].LatencyMs.ShouldBe(42);
    }

    [Fact]
    public async Task Revoking_account_access_cancels_an_automatic_measurement_and_does_not_restart_it()
    {
        var user = Store();
        var gateway = new StreamingGateway(automatic: true);
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway, _time);
        _time.Advance(TimeSpan.FromSeconds(1));
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        gateway.Authorized = false;
        gateway.Send(gateway.Status);
        await gateway.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(1));

        gateway.Starts.ShouldBe(1);
        catalog.Servers.ShouldAllBe(server => server.LatencyMs == null);
    }

    [Fact]
    public async Task A_ready_profile_is_visible_while_a_slower_profile_is_still_being_checked()
    {
        var user = Store();
        var gateway = new StreamingGateway();
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway);
        var first = user.Servers[0].Id;
        var second = user.Servers[1].Id;
        var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += (_, _) =>
        {
            if (catalog.Servers[0].LatencyMs == 42)
            {
                visible.TrySetResult();
            }
        };

        var refreshing = catalog.RefreshAsync(TestContext.Current.CancellationToken);
        gateway.Results.Writer.TryWrite(new(first, new LatencyResult(42, null, CheckedAt: _time.GetUtcNow(), Route: "vpn")));
        await visible.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        refreshing.IsCompleted.ShouldBeFalse("окно уже показывает первый ответ, не ожидая второй");
        catalog.Servers[0].IsMeasuringLatency.ShouldBeFalse();
        catalog.Servers[0].LatencyRoute.ShouldBe("vpn");
        catalog.Servers[1].IsMeasuringLatency.ShouldBeTrue();
        gateway.Results.Writer.TryWrite(new(second, new LatencyResult(null, LatencyFailure.NoResponse, "Timeout")));
        gateway.Results.Writer.Complete();
        (await refreshing).ShouldBeTrue();
        catalog.Servers.ShouldAllBe(server => !server.IsMeasuringLatency);
    }

    [Fact]
    public async Task A_late_result_after_switching_server_cannot_become_a_fresh_result_for_the_new_connection()
    {
        var user = Store();
        var gateway = new StreamingGateway(ignoreCancellation: true);
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway);
        var id = user.Servers[0].Id;
        var refreshing = catalog.RefreshAsync(TestContext.Current.CancellationToken);

        gateway.Send(gateway.Status with { State = ConnectionState.Connecting, ServerId = id });
        gateway.Results.Writer.TryWrite(new(id, new LatencyResult(17, null)));
        gateway.Results.Writer.Complete();

        (await refreshing).ShouldBeFalse();
        catalog.Servers[0].LatencyMs.ShouldBeNull("ответ относился к предыдущему пути");
        catalog.Servers.ShouldAllBe(server => !server.IsMeasuringLatency);
    }

    [Theory]
    [InlineData(ErrorCodes.ServerNotResponding, LatencyFailure.NoResponse, "Connection")]
    [InlineData(ErrorCodes.ServerNotFound, LatencyFailure.NoResponse, "NameResolution")]
    [InlineData(ErrorCodes.UnsupportedServer, LatencyFailure.Unsupported, null)]
    public void A_failed_actual_connection_replaces_the_previous_successful_latency(string code, LatencyFailure failure, string? diagnostic)
    {
        var user = Store();
        var gateway = new StreamingGateway();
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway);
        var id = user.Servers[0].Id;
        user.SetLatency(new Dictionary<string, LatencyResult> { [id] = new(12, null) });
        var succeededAt = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromSeconds(4));

        gateway.Send(gateway.Status with
        {
            State = ConnectionState.Error,
            ServerId = id,
            ErrorCode = code,
        });

        var profile = catalog.Servers[0];
        profile.LatencyMs.ShouldBeNull("старый положительный пинг не должен пережить отказ фактического подключения");
        profile.LatencyFailure.ShouldBe(failure);
        profile.LatencyDiagnostic.ShouldBe(diagnostic);
        profile.IsLatencyStale.ShouldBeFalse("последний известный результат — именно отказ подключения");
        profile.LastSuccessfulLatencyMs.ShouldBe(12);
        profile.LastSuccessfulLatencyAt.ShouldBe(succeededAt);
        var reopened = new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, _time).Servers[0];
        reopened.LatencyMs.ShouldBeNull();
        reopened.LastSuccessfulLatencyMs.ShouldBe(12);
        reopened.LastSuccessfulLatencyAt.ShouldBe(succeededAt);
    }

    [Fact]
    public void Upgrading_the_previous_schema_preserves_the_last_success_without_promoting_a_failed_result()
    {
        var user = Store();
        var first = user.Servers[0].Id;
        var second = user.Servers[1].Id;
        user.SetLatency(new Dictionary<string, LatencyResult>
        {
            [first] = new(42, null),
            [second] = new(null, LatencyFailure.NoResponse, "Timeout"),
        });
        using (var connection = new SqliteDatabase(_paths).Open())
        using (var command = connection.CreateCommand())
        {
            // База версии 8 из установленной 0.3.1: новых столбцов и экспериментальных таблиц еще нет.
            command.CommandText = "ALTER TABLE servers DROP COLUMN last_success_latency_ms; "
                + "ALTER TABLE servers DROP COLUMN last_success_latency_at; DROP TABLE retired_secret_keys; PRAGMA user_version = 8;";
            command.ExecuteNonQuery();
        }

        var upgraded = new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, _time);

        upgraded.Servers.Count.ShouldBe(2);
        upgraded.Servers[0].LastSuccessfulLatencyMs.ShouldBe(42);
        upgraded.Servers[0].LastSuccessfulLatencyAt.ShouldBe(_time.GetUtcNow());
        upgraded.Servers[1].LastSuccessfulLatencyMs.ShouldBeNull();
        upgraded.Servers[1].LatencyFailure.ShouldBe(LatencyFailure.NoResponse);
        upgraded.Definition(first).ShouldNotBeNull();
        upgraded.Definition(second).ShouldNotBeNull();
    }

    [Fact]
    public void A_saved_or_expired_result_is_history_until_remeasured_in_the_current_network()
    {
        var user = Store();
        var gateway = new StreamingGateway();
        using var catalog = new UserOrDemoCatalog(user, new DemoServerCatalog(_time), gateway);
        var id = user.Servers[0].Id;
        var completed = _time.GetUtcNow().AddSeconds(-2);
        user.SetLatency(new Dictionary<string, LatencyResult> { [id] = new(23, null, CheckedAt: completed, Route: "vpn") });
        catalog.Servers[0].LatencyCheckedAt.ShouldBe(completed, "сохраняется время службы, а не окончания всей серии");
        catalog.Servers[0].IsLatencyStale.ShouldBeFalse();

        _time.Advance(TimeSpan.FromMinutes(6));
        gateway.Send(gateway.Status);
        catalog.Servers[0].IsLatencyStale.ShouldBeTrue();
        catalog.Servers[0].LatencyMs.ShouldBe(23, "история сохраняется, но не выдается за текущий ответ");

        user.SetLatency(new Dictionary<string, LatencyResult> { [id] = new(25, null) });
        gateway.Send(gateway.Status with { NetworkChangedAt = _time.GetUtcNow() });
        catalog.Servers[0].IsLatencyStale.ShouldBeTrue();

        var reopened = new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, _time);
        reopened.Servers[0].IsLatencyStale.ShouldBeTrue("запуск окна не подтверждает прежнюю доступность");
        reopened.Servers[0].LatencyMs.ShouldBe(25);
    }

    private SqliteServerCatalog Store()
    {
        var store = new SqliteServerCatalog(new SqliteDatabase(_paths), _secrets, _time);
        store.Add(TestLinks
            .Select(link => LinkParser.Parse(link).Server.ShouldNotBeNull()).ToArray(), null);
        return store;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    private sealed class StreamingGateway(bool ignoreCancellation = false, bool automatic = false) : IGatewayClient
    {
        public Channel<KeyValuePair<string, LatencyResult>> Results { get; } = Channel.CreateUnbounded<KeyValuePair<string, LatencyResult>>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Starts { get; private set; }
        public bool Authorized { get; set; } = true;
        public bool HasReceivedStatus => automatic;
        public bool CanAutomaticallyMeasureLatency => automatic && Authorized;
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch);
        public ServiceKind Service => ServiceKind.Installed;
        public IReadOnlyList<JournalEntry> Journal => [];
        public event EventHandler<ConnectionStatus>? StatusChanged;
        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }
        public void Send(ConnectionStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        public async IAsyncEnumerable<KeyValuePair<string, LatencyResult>> MeasureLatencyProgressAsync(IReadOnlyList<string> serverIds,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Starts++;
            Started.TrySetResult();
            try
            {
                await foreach (var item in Results.Reader.ReadAllAsync(ignoreCancellation ? CancellationToken.None : cancellationToken))
                {
                    yield return item;
                }
            }
            finally
            {
                // Завершение итератора — само подтверждение отмены. Регистрация могла бы быть снята его
                // продолжением раньше, чем CTS вызовет ее после более нового callback канала.
                if (cancellationToken.IsCancellationRequested)
                {
                    Cancelled.TrySetResult();
                }
            }
        }
        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("должен использоваться поток готовых результатов");
        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DiagnosticReport([], DateTimeOffset.UnixEpoch));
    }
}
