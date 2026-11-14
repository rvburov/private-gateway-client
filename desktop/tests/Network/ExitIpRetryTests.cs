using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Фоновый IP без открытия страниц: искусственные часы и ответы, без подключения к реальным серверам.</summary>
public sealed class ExitIpRetryTests
{
    private const string Exit = "203.0.113.24";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_third_success_updates_the_status_without_any_ui_command(bool firstThrows)
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            return attempt == 1 && firstThrows ? throw new InvalidOperationException("test failure")
                : Task.FromResult<string?>(attempt < 3 ? null : Exit);
        });
        using var watch = new CancellationTokenSource();
        var (_, changes) = fixture.Gateway.Watch(watch.Token);
        var pending = fixture.Start();

        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(6));
        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        await pending.WaitAsync(Ct);

        calls.ShouldBe(3, "две неудачи не должны навсегда оставлять IP пустым");
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
        fixture.Gateway.Status.Protection.CheckedAt.ShouldNotBeNull();
        var published = new List<ConnectionStatus>();
        while (changes.TryRead(out var status))
        {
            published.Add(status);
        }
        published.ShouldContain(status => status.Exit.Ip == Exit, "главная получает измеренный IP обычным потоком состояния");
        fixture.Time.Advance(TimeSpan.FromHours(1));
        calls.ShouldBe(3, "после успеха новые попытки не нужны");
    }

    [Fact]
    public async Task A_manual_success_during_backoff_stops_the_worker_even_when_the_clock_has_not_advanced()
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) => Task.FromResult<string?>(Interlocked.Increment(ref calls) == 1 ? null : Exit));
        var pending = fixture.Start();
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));

        (await fixture.Gateway.CheckExitAsync(cancellationToken: Ct)).State.ShouldBe(CheckState.Ok);
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        calls.ShouldBe(2, "успешный ручной замер прекращает лишние автоматические запросы");
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_ip_from_the_previous_network_does_not_end_retries_after_new_failures(bool firstThrows)
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            var attempt = Interlocked.Increment(ref calls);
            return attempt == 1 && firstThrows ? throw new InvalidOperationException("test failure")
                : Task.FromResult<string?>(attempt < 3 ? null : Exit);
        });
        fixture.Update(status => status with
        {
            NetworkChangedAt = fixture.Time.GetUtcNow(),
            Exit = status.Exit with { Ip = "203.0.113.25" },
            Protection = status.Protection with { CheckedAt = fixture.Time.GetUtcNow().AddMinutes(-1) },
        });
        var pending = fixture.Start();

        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(6));
        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        calls.ShouldBe(3, "сохраненные IP и CheckedAt не подтверждают новую сеть после отказа пробы");
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
        fixture.Gateway.Status.Protection.CheckedAt.ShouldBe(fixture.Time.GetUtcNow());
    }

    [Fact]
    public async Task Traffic_updates_do_not_accelerate_or_postpone_the_next_probe()
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) => Task.FromResult<string?>(Interlocked.Increment(ref calls) == 1 ? null : Exit));
        var pending = fixture.Start();
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));

        fixture.Update(status => status with { Traffic = status.Traffic with { DownloadMbps = 1 } });
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(2), "пробуждение не начинает отсчет заново");
        calls.ShouldBe(1, "счетчики трафика не вызывают запрос до срока");
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        calls.ShouldBe(2);
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
    }

    [Fact]
    public async Task Retry_delay_grows_to_a_cap_and_disconnect_stops_future_requests()
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        });
        var pending = fixture.Start();
        foreach (var seconds in new[] { 3, 6, 12, 24, 30, 30 })
        {
            (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(seconds));
            fixture.Time.Advance(TimeSpan.FromSeconds(seconds));
        }
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(30));
        calls.ShouldBe(7);

        fixture.Update(status => status with { State = ConnectionState.Off, Protection = ProtectionChecks.For(ConnectionState.Off) });
        await pending.WaitAsync(Ct);
        fixture.Time.Advance(TimeSpan.FromMinutes(10));

        calls.ShouldBe(7);
        fixture.Gateway.Status.Exit.Ip.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pause_or_missing_network_prevents_requests_until_the_state_recovers(bool paused)
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) => Task.FromResult<string?>(Interlocked.Increment(ref calls) == 1 ? null : Exit));
        var pending = fixture.Start();
        (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));
        fixture.Update(status => paused
            ? status with { CaptivePauseUntil = fixture.Time.GetUtcNow().AddMinutes(5) }
            : status with { NetworkUnavailable = true });
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        // Даже если таймер и событие пришли вместе, проверка состояния предшествует началу сетевого запроса.
        calls.ShouldBe(1);

        fixture.Update(status => status with { CaptivePauseUntil = null, NetworkUnavailable = false });
        await pending.WaitAsync(Ct);

        calls.ShouldBe(2);
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
    }

    [Fact]
    public async Task Replacing_the_network_cancels_the_old_measurement_and_only_the_new_ip_is_published()
    {
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken previous = default;
        await using var fixture = new Fixture((_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                previous = token;
                return late.Task;
            }
            return Task.FromResult<string?>(Exit);
        });
        var old = fixture.Start();
        fixture.Update(status => status with { NetworkChangedAt = fixture.Time.GetUtcNow().AddSeconds(1) });

        var current = fixture.Start();
        await current.WaitAsync(Ct);
        previous.IsCancellationRequested.ShouldBeTrue();
        late.SetResult("198.51.100.7");
        await old.WaitAsync(Ct);

        calls.ShouldBe(2);
        fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
        fixture.Gateway.Status.HasExitIpLeak.ShouldBeFalse();
    }

    [Fact]
    public async Task An_old_waiter_does_not_start_a_probe_in_a_new_session()
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>(null);
        });
        var pending = fixture.Start();
        await fixture.Time.NextDelay();

        Set(fixture.Gateway, "_session", 1);
        fixture.Update(status => status with { ServerId = "next-server", Since = fixture.Time.GetUtcNow().AddSeconds(1) });
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        await pending.WaitAsync(Ct);

        calls.ShouldBe(1);
        fixture.Gateway.Status.Exit.Ip.ShouldBeNull();
    }

    [Fact]
    public async Task Disabled_automatic_checks_do_not_start_a_background_ip_request()
    {
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            calls++;
            return Task.FromResult<string?>(Exit);
        }, enabled: false);

        await fixture.Start().WaitAsync(Ct);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        calls.ShouldBe(0);
        fixture.Gateway.Status.Exit.Ip.ShouldBeNull();
    }

    [Fact]
    public async Task A_proxy_probe_does_not_begin_its_second_stage_after_disconnect()
    {
        var direct = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = new Fixture((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return direct.Task;
        });
        fixture.Update(status => status with { Mode = ConnectionMode.Proxy });
        Set(fixture.Gateway, "_probe", new ProbeLink(1080, "test", "not-a-real-password"));
        var pending = fixture.Gateway.CheckExitAsync(cancellationToken: Ct);

        fixture.Update(status => status with { State = ConnectionState.Off });
        direct.SetResult("198.51.100.7");

        (await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Reason.ShouldBe("session_changed");
        calls.ShouldBe(1, "отключение после прямой стадии запрещает следующую прокси-пробу");
    }

    [Theory]
    [InlineData("off")]
    [InlineData("reconnecting")]
    [InlineData("paused")]
    [InlineData("unavailable")]
    public async Task An_in_flight_attempt_is_cancelled_when_protection_changes_and_resume_keeps_the_worker(string change)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = new Fixture(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                using var registration = token.Register(() => cancelled.TrySetResult());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Exit;
        });
        var pending = fixture.Start();
        fixture.Update(status => change switch
        {
            "off" => status with { State = ConnectionState.Off },
            "reconnecting" => status with { State = ConnectionState.Reconnecting },
            "paused" => status with { CaptivePauseUntil = fixture.Time.GetUtcNow().AddMinutes(5) },
            _ => status with { NetworkUnavailable = true },
        });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        if (change is "paused" or "unavailable")
        {
            fixture.Update(status => status with { CaptivePauseUntil = null, NetworkUnavailable = false });
            (await fixture.Time.NextDelay()).ShouldBe(TimeSpan.FromSeconds(3));
            fixture.Time.Advance(TimeSpan.FromSeconds(3));
            await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            calls.ShouldBe(2);
            fixture.Gateway.Status.Exit.Ip.ShouldBe(Exit);
        }
        else
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            calls.ShouldBe(1);
            fixture.Gateway.Status.Exit.Ip.ShouldBeNull();
        }
        fixture.Journal.Tail(50).ShouldNotContain(e => e.Message.StartsWith("Проверка exit_ip_retry не выполнена:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stopping_the_service_cancels_an_in_flight_ip_measurement()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = new Fixture(async (_, token) =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        var pending = fixture.Start();

        await fixture.DisposeAsync();
        await cancelled.Task.WaitAsync(Ct);
        await pending.WaitAsync(Ct);

        fixture.Gateway.Status.Exit.Ip.ShouldBeNull();
        fixture.Journal.Tail(50).ShouldNotContain(e => e.Message.StartsWith("Проверка exit_ip_retry не выполнена:", StringComparison.Ordinal));
    }

    private static void Set(Gateway gateway, string field, object value) =>
        typeof(Gateway).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(gateway, value);

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(Func<ServerProbe, CancellationToken, Task<string?>> measure, bool enabled = true)
        {
            Journal = new ServiceJournal(Time);
            Gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"),
                Trace: new Uri("https://example.com/trace"), ExitIp: measure, BlockWatch: TimeSpan.FromHours(12),
                TrafficWatch: Timeout.InfiniteTimeSpan), Time, Journal);
            var request = new CoreRequest(new ServerDefinition("ip-retry", string.Empty, ProxyProtocol.Vless, "vpn.example.com", 8443,
                    TransportOptions.Tcp, new SecurityOptions(TransportSecurity.Tls, "vpn.example.com"), ProtocolOptions.Empty,
                    new ServerCredentials(Uuid: "11111111-2222-3333-4444-555555555555")),
                ConnectionMode.Vpn, ProtectionSettings.Default with { CheckLeaksAfterConnect = enabled }, ConnectionSettings.Default, RoutingSettings.Default);
            Set(Gateway, "_request", request);
            Set(Gateway, "_status", ConnectionStatus.Initial(Time.GetUtcNow()) with
            {
                State = ConnectionState.Connected,
                Mode = ConnectionMode.Vpn,
                ServerId = "ip-retry",
                Protection = ProtectionChecks.For(ConnectionState.Connected),
            });
            Set(Gateway, "_directIp", "198.51.100.7");
        }

        public RecordedTime Time { get; } = new();
        public ServiceJournal Journal { get; }
        public Gateway Gateway { get; }

        public Task Start() => (Task)typeof(Gateway).GetMethod("StartExitIpRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Gateway, [0])!;

        public void Update(Func<ConnectionStatus, ConnectionStatus> change) =>
            typeof(Gateway).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Gateway, [change]);

        public ValueTask DisposeAsync() => Gateway.DisposeAsync();
    }

    /// <summary>Сигнал только после установки таймера: тест не двигает часы раньше регистрации ожидания.</summary>
    private sealed class RecordedTime : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => _clock.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime <= TimeSpan.FromSeconds(30))
            {
                _delays.Writer.TryWrite(dueTime);
            }
            return timer;
        }

        public void Advance(TimeSpan delay) => _clock.Advance(delay);

        public async Task<TimeSpan> NextDelay() => await _delays.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }
}
