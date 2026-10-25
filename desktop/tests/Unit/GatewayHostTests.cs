using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Служба для окна (<see cref="GatewayHost"/>): найти при запуске или позже, запустить службу
/// пользователя для режима прокси, честная ошибка без службы. Службы — подделки, адреса — выдуманные.
/// </summary>
public sealed class GatewayHostTests
{
    private static readonly ServerProfile Server =
        new("srv-1", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    private static readonly ServiceAddress Installed = new("installed", System: true);
    private static readonly ServiceAddress User = new("user", System: false);

    private readonly FakeTimeProvider _time = new();
    private readonly HashSet<ServiceAddress> _answering = [];
    private readonly Dictionary<ServiceAddress, FakeService> _services = [];
    private readonly FakeLauncher _launcher;
    private AppSettings _settings = AppSettings.Default;

    public GatewayHostTests() => _launcher = new FakeLauncher(() => _answering.Add(User));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_a_service_connect_is_an_error_and_the_journal_says_why()
    {
        using var host = Host();
        var seen = new List<ConnectionState>();
        host.StatusChanged += (_, s) => seen.Add(s.State);

        await host.ConnectAsync(Server, Ct);
        await host.ConnectAsync(Server, Ct);

        (host.Status.State, host.Status.ErrorCode, host.Status.ServerId).ShouldBe((ConnectionState.Error, ErrorCodes.ServiceNotRunning, "srv-1"));
        seen.ShouldBe([ConnectionState.Error, ConnectionState.Error], "каждое нажатие — новая ошибка, экран показывается снова");
        _launcher.Started.ShouldBe(0, "в режиме VPN служба пользователя не поможет — ей нужны права");
        host.Journal.ShouldHaveSingleItem().Message.ShouldContain("Служба не найдена", Case.Sensitive, "одна и та же запись не повторяется");
        host.Service.ShouldBe(ServiceKind.None);
    }

    [Fact]
    public async Task Service_started_after_the_window_is_found_on_connect()
    {
        using var host = Host();
        _answering.Add(Installed);

        await host.ConnectAsync(Server, Ct);

        _services[Installed].Connected.ShouldBe(["srv-1"]);
        _launcher.Started.ShouldBe(0);
    }

    [Fact]
    public async Task Installed_service_found_at_start_is_used_even_in_proxy_mode()
    {
        _answering.Add(Installed);
        _answering.Add(User);
        _settings = _settings with { Mode = ConnectionMode.Proxy };
        using var host = Host();

        await host.ConnectAsync(Server, Ct);

        _services.Keys.ShouldBe([Installed]);
        _services[Installed].Connected.ShouldBe(["srv-1"]);
    }

    [Fact]
    public async Task Proxy_mode_starts_the_user_service_and_connects_through_it()
    {
        _settings = _settings with { Mode = ConnectionMode.Proxy };
        using var host = Host();

        await host.ConnectAsync(Server, Ct);

        _launcher.Started.ShouldBe(1);
        _services[User].Connected.ShouldBe(["srv-1"]);
        host.Journal.ShouldContain(e => e.Source == "app" && e.Message.StartsWith("Запущена служба пользователя", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_service_that_cannot_start_is_an_error_with_the_reason()
    {
        _settings = _settings with { Mode = ConnectionMode.Proxy };
        _launcher.Fails = true;
        using var host = Host();

        await host.ConnectAsync(Server, Ct);

        host.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning);
        host.Journal.ShouldHaveSingleItem().Message.ShouldContain("файла службы нет");
    }

    [Fact]
    public async Task User_service_that_does_not_answer_is_an_error_after_the_wait()
    {
        _settings = _settings with { Mode = ConnectionMode.Proxy };
        using var host = new GatewayHost(new ServiceLocator([Installed, User], _ => false, _launcher, _time), Attach, () => _settings, _time);

        var started = _time.GetUtcNow();
        var connect = host.ConnectAsync(Server, Ct);
        for (var i = 0; i < 1000 && !connect.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Delay(5, Ct);
        }

        await connect;
        (_time.GetUtcNow() - started).ShouldBeGreaterThanOrEqualTo(ServiceLocator.UserServiceStart, "ждали ответа все 10 с, а не бросили сразу");
        _launcher.Started.ShouldBe(1);
        host.Status.ErrorCode.ShouldBe(ErrorCodes.ServiceNotRunning);
        host.Journal.ShouldHaveSingleItem().Message.ShouldContain("не ответила за 10 с");
    }

    [Fact]
    public async Task Lost_service_is_looked_for_again_and_replaced()
    {
        _answering.Add(Installed);
        using var host = Host();
        var lost = _services[Installed];
        lost.Raise(Lost(lost.Status));
        _answering.Remove(Installed);
        _settings = _settings with { Mode = ConnectionMode.Proxy };

        await host.ConnectAsync(Server, Ct);

        lost.Disposed.ShouldBeTrue();
        lost.Connected.ShouldBeEmpty();
        _services[User].Connected.ShouldBe(["srv-1"], "пропавшая установленная служба — запускается служба пользователя");
        var states = new List<ConnectionState>();
        host.StatusChanged += (_, st) => states.Add(st.State);
        lost.Raise(ConnectionStatus.Initial(_time.GetUtcNow()));
        states.ShouldBeEmpty("прежняя служба окну больше не говорит");
    }

    [Fact]
    public async Task Lost_service_without_a_replacement_is_asked_again()
    {
        _answering.Add(Installed);
        using var host = Host();
        var lost = _services[Installed];
        lost.Raise(Lost(lost.Status));
        _answering.Remove(Installed);

        await host.ConnectAsync(Server, Ct);

        lost.Connected.ShouldBe(["srv-1"], "ее клиент сам ответит новой ошибкой — экран покажется снова");
    }

    [Fact]
    public async Task System_proxy_left_after_a_crash_is_given_back_at_start()
    {
        using var host = new GatewayHost(new ServiceLocator([Installed, User], _answering.Contains, _launcher, _time), Attach, () => _settings, _time,
            staleProxy: () => true);

        for (var i = 0; i < 200 && !_services.ContainsKey(User); i++)
        {
            await Task.Delay(10, Ct);
        }

        _launcher.Started.ShouldBe(1, "служба пользователя возвращает снимок при запуске — окно поднимает ее сразу, даже в режиме VPN");
        _services.Keys.ShouldBe([User]);
        host.Journal.ShouldHaveSingleItem().Message.ShouldStartWith("Системный прокси остался после сбоя");
        using var clean = Host();
        _launcher.Started.ShouldBe(1, "без снимка при запуске ничего не запускается");
    }

    [Fact]
    public async Task Checks_without_a_service_do_not_start_one()
    {
        _settings = _settings with { Mode = ConnectionMode.Proxy };
        using var host = Host();

        var report = await host.RunChecksAsync(Ct);

        report.Results.ShouldHaveSingleItem().ShouldBe(new DiagnosticResult(DiagnosticCheck.Service, CheckState.Stopped, "not_found"));
        _launcher.Started.ShouldBe(0);
    }

    [Fact]
    public async Task Ping_in_proxy_mode_starts_the_user_service()
    {
        using var host = Host();
        (await host.MeasureLatencyAsync(["srv-1"], Ct)).ShouldBeEmpty("режим VPN — службы нет, пинга нет");
        _settings = _settings with { Mode = ConnectionMode.Proxy };

        var results = await host.MeasureLatencyAsync(["srv-1"], Ct);

        results.ShouldBe(new Dictionary<string, LatencyResult> { ["srv-1"] = new(42, null) });
        _launcher.Started.ShouldBe(1);
    }

    [Fact]
    public async Task Disconnect_without_a_service_clears_the_error()
    {
        using var host = Host();
        await host.ConnectAsync(Server, Ct);

        await host.DisconnectAsync(Ct);

        host.Status.State.ShouldBe(ConnectionState.Off);
    }

    /// <summary>Так клиент службы показывает, что служба пропала.</summary>
    private ConnectionStatus Lost(ConnectionStatus status) =>
        (status.MoveTo(ConnectionState.Connecting, _time.GetUtcNow()) with { ErrorCode = ErrorCodes.ServiceNotRunning }).MoveTo(ConnectionState.Error, _time.GetUtcNow());

    private GatewayHost Host() => new(new ServiceLocator([Installed, User], _answering.Contains, _launcher, _time), Attach, () => _settings, _time);

    private FakeService Attach(ServiceAddress address) => _services[address] = new FakeService(_time);

    private sealed class FakeLauncher(Action started) : IUserServiceLauncher
    {
        public int Started { get; private set; }

        public bool Fails { get; set; }

        public bool Start()
        {
            if (Fails)
            {
                return false;
            }

            Started++;
            started();
            return true;
        }
    }

    /// <summary>Служба, которая запоминает просьбы и сообщает заданное состояние.</summary>
    private sealed class FakeService(TimeProvider time) : IGatewayClient, IDisposable
    {
        public List<string> Connected { get; } = [];

        public bool Disposed { get; private set; }

        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Initial(time.GetUtcNow());

        public ServiceKind Service => ServiceKind.Installed;

        public IReadOnlyList<JournalEntry> Journal => [];

        public event EventHandler<ConnectionStatus>? StatusChanged;

        public event EventHandler<JournalEntry>? JournalAdded { add { } remove { } }

        public void Raise(ConnectionStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, status);
        }

        public Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default)
        {
            Connected.Add(server.Id);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ResumeProtectionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiagnosticReport([], time.GetUtcNow()));

        public Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LatencyResult>>(serverIds.ToDictionary(id => id, _ => new LatencyResult(42, null)));

        public void Dispose() => Disposed = true;
    }
}
