using System.Net;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Подготовка подключения и потеря firewall — только подделки, без ядер, TUN, прав и внешней сети.
/// Ответ DNS можно задержать до следующего действия пользователя, а состояние ОС — менять отдельно от политики службы.
/// </summary>
public sealed class GatewayLifecycleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-gateway-lifecycle-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disconnect_during_address_lookup_prevents_both_late_connection_and_late_refusal(bool found)
    {
        var entered = NewSignal();
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firewall = new FakeFirewall { Fails = true };
        await using var gateway = Create(firewall, resolve: (_, _) =>
        {
            entered.TrySetResult();
            return answer.Task;
        });

        var pending = gateway.ConnectAsync(Request("slow.example.com", "old"), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Connecting);
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        answer.SetResult(found ? [IPAddress.Parse("198.51.100.20")] : []);

        (await pending).Accepted.ShouldBeFalse("пользователь уже отменил запрос, ответ относится к прежнему действию");
        gateway.Status.State.ShouldBe(ConnectionState.Off, "ни успех, ни ошибка прежнего DNS не включают защиту снова");
        firewall.Enables.ShouldBe(0, "после отключения не должно быть даже попытки изменить firewall");
    }

    [Fact]
    public async Task A_late_address_lookup_does_not_replace_a_newer_server_or_its_error()
    {
        var entered = NewSignal();
        var answer = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firewall = new FakeFirewall { Fails = true };
        await using var gateway = Create(firewall, resolve: (_, _) =>
        {
            entered.TrySetResult();
            return answer.Task;
        });

        var pending = gateway.ConnectAsync(Request("slow.example.com", "old"), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await gateway.ConnectAsync(Request("198.51.100.30", "new"), TestContext.Current.CancellationToken);
        var latest = gateway.Status;
        answer.SetResult([IPAddress.Parse("198.51.100.20")]);

        (await pending).Accepted.ShouldBeFalse();
        gateway.Status.ShouldBe(latest, "итог нового подключения, включая его ошибку, принадлежит новому серверу");
        gateway.Status.ServerId.ShouldBe("new");
        firewall.Enables.ShouldBe(1, "поздний запрос не меняет правила и не запускает старый сервер");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_firewall_that_cannot_be_restored_is_an_error_and_never_claims_stopped_traffic(bool throws)
    {
        var clock = new FakeTimeProvider();
        var firewall = new FakeFirewall { Enabled = true };
        var boot = Path.Combine(_root, "block.json");
        BootBlock.Save(boot, new HeldBlock(new KillSwitchPolicy(null, []), string.Empty, Boot: true, Strict: true));
        await using var service = await RunningService.StartAsync(time: clock, options: o => o with
        {
            KillSwitch = firewall,
            BootBlock = boot,
            BlockWatch = TimeSpan.FromSeconds(1),
        });
        var gateway = service.Gateway;
        gateway.Status.State.ShouldBe(ConnectionState.Blocked);

        firewall.Enabled = false;
        firewall.Fails = throws;
        firewall.DoesNothing = !throws;
        clock.Advance(TimeSpan.FromSeconds(1));

        gateway.Status.State.ShouldBe(ConnectionState.Error, "нет подтвержденных правил — нельзя говорить «Блокировка»");
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.KillSwitchFailed);
        var checks = gateway.Status.Protection;
        (checks.AllTraffic, checks.KillSwitch, checks.Dns, checks.Ipv6)
            .ShouldBe((CheckState.Warning, CheckState.Warning, CheckState.Warning, CheckState.Warning));
        (await gateway.CheckExitAsync()).State.ShouldBe(CheckState.Warning, "отчет проверки тоже не обещает остановленный трафик");
        var report = await service.Client.RunChecksAsync(new V1.RunChecksRequest(), cancellationToken: TestContext.Current.CancellationToken);
        report.Results.Single(r => r.Id == DiagnosticCheck.Core.ToContractId()).State.ShouldBe(V1.CheckState.Warning,
            "ядро могло остаться живым: его сохраняют до возврата firewall");
        report.Results.Single(r => r.Id == DiagnosticCheck.AllTraffic.ToContractId()).Reason.ShouldBe("firewall",
            "потерю firewall нельзя объяснять раздельной маршрутизацией");

        await gateway.DisconnectAsync(keepBlock: true, cancellationToken: TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Error, "строгий режим не превращает известную потерю firewall в «Блокировку»");
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Warning);

        firewall.Fails = false;
        firewall.DoesNothing = false;
        clock.Advance(TimeSpan.FromSeconds(1));

        firewall.IsIntact.ShouldBeTrue("сторож продолжает пробовать восстановить сохраненную политику после ошибки");
        gateway.Status.State.ShouldBe(ConnectionState.Error, "автоматически выбирать или запускать сервер после ошибки нельзя");
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Stopped, "только подтвержденный ОС блок останавливает трафик");
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Ok);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Startup_failure_preserves_the_saved_policy_and_retries_without_claiming_off_or_blocked(bool oldRules, bool throws)
    {
        var clock = new FakeTimeProvider();
        var firewall = new FakeFirewall { Enabled = oldRules, Fails = throws, DoesNothing = !throws };
        var boot = Path.Combine(_root, "failed-start.json");
        BootBlock.Save(boot, new HeldBlock(new KillSwitchPolicy(null, [new(IPAddress.Parse("198.51.100.20"), 443)]),
            "vpn.example.com", Boot: true, Strict: true));
        await using var gateway = Create(firewall, clock, boot: boot, canVpn: false);

        gateway.Status.State.ShouldBe(ConnectionState.Error, "неудача восстановления не отменяет защиту при загрузке");
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Warning);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Warning);
        BootBlock.Read(boot).ShouldNotBeNull().Host.ShouldBe("vpn.example.com");
        var attempted = firewall.Enables;

        firewall.Fails = false;
        firewall.DoesNothing = false;
        clock.Advance(TimeSpan.FromSeconds(1));

        firewall.Enables.ShouldBeGreaterThan(attempted, "целые прежние правила не заменяют восстановление нужной сохраненной политики");
        firewall.IsIntact.ShouldBeTrue();
        gateway.Status.Protection.AllTraffic.ShouldBe(CheckState.Stopped);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Ok);
    }

    [Fact]
    public async Task Declared_but_inactive_startup_filters_never_look_like_a_working_block()
    {
        var firewall = new FakeFirewall { Enabled = true, Intact = false };
        var boot = Path.Combine(_root, "inactive-start.json");
        BootBlock.Save(boot, new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: false));
        await using var gateway = Create(firewall, boot: boot, canVpn: false);

        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Warning, "успех команды Enable не доказывает действие фильтров");
        (await gateway.CheckExitAsync()).State.ShouldBe(CheckState.Warning);
    }

    private Gateway Create(FakeFirewall firewall, TimeProvider? time = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, string? boot = null, bool canVpn = true)
    {
        // Существующий файл проходит проверку наличия ядер; запуск до него не дойдет — fake firewall отказывает.
        // Несуществующее сетевое пространство изолирует также поиск оставшихся ip rules в конструкторе Linux.
        var file = typeof(Gateway).Assembly.Location;
        return new Gateway(new GatewayOptions(file, canVpn, new Uri("https://example.com/"), SingBoxPath: file, KillSwitch: firewall,
            TunnelNetns: Path.Combine(_root, "no-network-namespace"), Resolve: resolve, BootBlock: boot, BlockWatch: TimeSpan.FromSeconds(1)),
            time ?? TimeProvider.System, new ServiceJournal(time ?? TimeProvider.System));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static V1.ConnectRequest Request(string host, string id)
    {
        var server = new ServerDefinition("test", string.Empty, ProxyProtocol.Vless, host, 443, TransportOptions.Tcp,
            new SecurityOptions(TransportSecurity.Tls, Sni: "example.com"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: "11111111-2222-3333-4444-555555555555"));
        var settings = AppSettings.Default with
        {
            Protection = ProtectionSettings.Default with { CheckLeaksAfterConnect = false, BootProtection = false },
        };
        return new V1.ConnectRequest
        {
            Server = server.ToContract(id), Mode = V1.Mode.Vpn,
            Protection = settings.ToContract(), Connection = settings.ToConnection(), Routing = settings.ToRouting(),
        };
    }

    private sealed class FakeFirewall : IKillSwitch
    {
        public string Name => "fake";
        public bool IsEnabled => Enabled;
        public bool IsIntact => Enabled && Intact;
        public bool Enabled { get; set; }
        public bool Intact { get; set; } = true;
        public bool Fails { get; set; }
        public bool DoesNothing { get; set; }
        public int Enables { get; private set; }

        public void Enable(KillSwitchPolicy policy)
        {
            Enables++;
            if (Fails)
            {
                throw new IOException("Отказ подделки firewall; правил ОС тест не касается");
            }

            if (!DoesNothing)
            {
                Enabled = true;
            }
        }

        public void Disable() => Enabled = false;
    }
}
