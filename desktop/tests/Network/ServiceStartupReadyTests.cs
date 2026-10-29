using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>systemd ждет настоящий блок при загрузке, а не запуск процесса; все правила здесь — подделка.</summary>
public sealed class ServiceStartupReadyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-startup-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Ready_is_sent_after_gateway_restores_saved_boot_protection()
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: false));
        var killSwitch = new StartupSwitch();
        var notified = false;
        await using var gateway = await ServiceStartupReady.CreateGatewayAsync(Options(block, killSwitch), TimeProvider.System,
            new ServiceJournal(TimeProvider.System), "/run/audit-notify.sock", socket =>
            {
                socket.ShouldBe("/run/audit-notify.sock");
                killSwitch.IsIntact.ShouldBeTrue("READY=1 до восстановления блока выпустил бы сеть раньше защиты");
                notified = true;
            });

        notified.ShouldBeTrue();
        killSwitch.Enables.ShouldBe(1, "правила восстанавливает Gateway, а не отдельная несогласованная копия логики");
    }

    [Fact]
    public async Task Failed_boot_protection_never_releases_systemd()
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: false));
        var killSwitch = new StartupSwitch { Refuse = true };
        var notified = false;

        await Should.ThrowAsync<IOException>(async () =>
        {
            await using var gateway = await ServiceStartupReady.CreateGatewayAsync(Options(block, killSwitch), TimeProvider.System,
                new ServiceJournal(TimeProvider.System), "/run/audit-notify.sock", _ => notified = true);
        });

        notified.ShouldBeFalse("запуск процесса без действующего блока — еще не готовность");
        killSwitch.IsIntact.ShouldBeFalse();
    }

    [Fact]
    public async Task Intact_old_rules_do_not_release_systemd_when_the_saved_policy_could_not_be_restored()
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: false));
        var killSwitch = new StartupSwitch { Enabled = true, Intact = true, Refuse = true };
        var notified = false;

        await Should.ThrowAsync<IOException>(async () =>
        {
            await using var gateway = await ServiceStartupReady.CreateGatewayAsync(Options(block, killSwitch), TimeProvider.System,
                new ServiceJournal(TimeProvider.System), "/run/audit-notify.sock", _ => notified = true);
        });

        notified.ShouldBeFalse("старые фильтры могли оставить разрешение прежнему TUN; нужна готовность новой политики");
    }

    [Fact]
    public void Declared_but_inactive_filters_do_not_count_as_ready()
    {
        var killSwitch = new StartupSwitch { Enabled = true, Intact = false };
        var notified = false;

        Should.Throw<IOException>(() => ServiceStartupReady.NotifySystemd("@audit-notify", null, killSwitch, _ => notified = true));

        notified.ShouldBeFalse("наличие таблицы без действующих правил не должно открывать барьер");
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void Disabled_or_open_boot_policy_does_not_require_filters(bool boot, bool paused, bool open)
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", boot, Strict: false, paused, open));
        var notified = false;

        ServiceStartupReady.NotifySystemd("@audit-notify", block, null, _ => notified = true);

        notified.ShouldBeTrue("пользователь выключил постоянную защиту — блок до сети не требуется");
    }

    [Fact]
    public void Pause_left_by_a_crash_requires_protection_even_without_boot_policy()
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: false, Strict: false, Paused: true));

        Should.Throw<IOException>(() => ServiceStartupReady.NotifySystemd("@audit-notify", block, null,
            _ => throw new InvalidOperationException("не должно уведомлять systemd")));
    }

    [Fact]
    public void Manual_start_does_not_send_systemd_notifications()
    {
        var block = Save(new HeldBlock(new KillSwitchPolicy(null, []), "vpn.example.com", Boot: true, Strict: false));

        ServiceStartupReady.NotifySystemd(null, block, null, _ => throw new InvalidOperationException("обычный запуск — без NOTIFY_SOCKET"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ready_reaches_the_notify_socket_as_a_local_datagram(bool abstractSocket)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "уведомление systemd — локальный сокет Linux");
        var address = abstractSocket ? "@pg-ready-" + Guid.NewGuid().ToString("N") : Path.Combine(_root, "notify.sock");
        var endpoint = new UnixDomainSocketEndPoint(abstractSocket ? "\0" + address[1..] : address);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        listener.Bind(endpoint);
        var received = new byte[32];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        SystemdService.NotifyReady(address);

        var count = await listener.ReceiveAsync(received, SocketFlags.None, timeout.Token);
        Encoding.UTF8.GetString(received, 0, count).ShouldBe("READY=1");
    }

    private string Save(HeldBlock block)
    {
        var path = Path.Combine(_root, "boot-block.json");
        BootBlock.Save(path, block);
        return path;
    }

    private static GatewayOptions Options(string block, IKillSwitch killSwitch) => new(null, false, new Uri("https://example.com/"),
        KillSwitch: killSwitch, BootBlock: block, BlockWatch: TimeSpan.FromHours(1));

    private sealed class StartupSwitch : IKillSwitch
    {
        public string Name => "подделка раннего блока";
        public bool Enabled { get; set; }
        public bool Intact { get; set; } = true;
        public bool Refuse { get; init; }
        public int Enables { get; private set; }
        public bool IsEnabled => Enabled;
        public bool IsIntact => Enabled && Intact;

        public void Enable(KillSwitchPolicy policy)
        {
            Enables++;
            if (Refuse)
            {
                throw new IOException("проверочный отказ в установке блока");
            }

            Enabled = true;
        }

        public void Disable() => Enabled = false;
    }
}
