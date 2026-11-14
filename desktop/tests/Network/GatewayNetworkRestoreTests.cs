using System.Net;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Ipc;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Явное отключение: отказы ОС не превращаются в успешный ответ, повтор не требует живого туннеля.</summary>
public sealed class GatewayNetworkRestoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-restore-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Firewall_failure_still_restores_dns_and_forgets_boot_then_allows_retry(bool throws)
    {
        var clock = new FakeTimeProvider();
        var firewall = new Firewall { Enabled = true, Refuse = true, Throws = throws };
        var dns = new Dns();
        var block = Path.Combine(_root, "block.json");
        BootBlock.Save(block, new HeldBlock(new KillSwitchPolicy(null, []), "example.com", Boot: true, Strict: true));
        await using var gateway = Create(firewall, dns, clock, block);
        gateway.Status.State.ShouldBe(ConnectionState.Blocked);

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));

        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);
        gateway.Status.Protection.KillSwitch.ShouldBe(CheckState.Warning);
        dns.Restores.ShouldBe(1, "отказ firewall не отменяет восстановление DNS");
        File.Exists(block).ShouldBeFalse("после явного выхода прежний блок не возвращается при загрузке");
        var enables = firewall.Enables;
        clock.Advance(TimeSpan.FromSeconds(2));
        firewall.Enables.ShouldBe(enables, "выход отменяет прежнее намерение восстанавливать блок сторожем");

        firewall.Refuse = false;
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        firewall.Enabled.ShouldBeFalse();
        gateway.Status.State.ShouldBe(ConnectionState.Off);
        gateway.Status.ErrorCode.ShouldBeNull();
    }

    [Fact]
    public async Task Dns_failure_without_a_tunnel_is_reported_and_retried_without_reconnecting()
    {
        var dns = new Dns { Refuse = true };
        var firewall = new Firewall { Enabled = true };
        await using var gateway = Create(firewall, dns);

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));
        firewall.Enabled.ShouldBeFalse("DNS не восстановлен, но собственный firewall уже можно снять");
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.Protection.Dns.ShouldBe(CheckState.Warning);

        dns.Refuse = false;
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        dns.Restores.ShouldBe(2, "повтор очищает снимок DNS даже без _tunnel");
        gateway.Status.State.ShouldBe(ConnectionState.Off);
    }

    [Fact]
    public async Task Cleanup_error_from_initial_off_is_not_reported_as_success()
    {
        var dns = new Dns { Refuse = true };
        await using var gateway = Create(new Firewall(), dns);
        gateway.Status.State.ShouldBe(ConnectionState.Off);

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);
        dns.Refuse = false;
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Off);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Undeletable_boot_state_prevents_success_but_not_other_cleanup(bool connection)
    {
        var path = Path.Combine(_root, "boot-state");
        var firewall = new Firewall { Enabled = true };
        var dns = new Dns();
        // Каталог вместо файла воспроизводит отказ File.Delete на всех ОС, даже у root.
        Directory.CreateDirectory(path);
        await using var gateway = Create(firewall, dns, block: connection ? null : path,
            boot: connection ? new BootConnection(path, new Sealer()) : null);

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));
        firewall.Enabled.ShouldBeFalse();
        dns.Restores.ShouldBe(1);
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);

        Directory.Delete(path);
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Off);
    }

    [Fact]
    public async Task Strict_manual_disconnect_keeps_the_requested_block()
    {
        var firewall = new Firewall { Enabled = true };
        var block = Path.Combine(_root, "strict.json");
        BootBlock.Save(block, new HeldBlock(new KillSwitchPolicy(null, []), "example.com", Boot: true, Strict: true));
        await using var gateway = Create(firewall, new Dns(), block: block);

        await gateway.DisconnectAsync(keepBlock: true, cancellationToken: TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Blocked);
        firewall.Enabled.ShouldBeTrue("обычное отключение со строгим режимом сохраняет выбранную защиту");
        File.Exists(block).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task System_proxy_restore_failure_blocks_success_and_retries_without_a_tunnel(bool throws)
    {
        var proxy = new FakeSystemProxy();
        var clock = TimeProvider.System;
        var journal = new ServiceJournal(clock);
        var backup = Path.Combine(_root, "system-proxy.json");
        var guard = new SystemProxyGuard(proxy, backup, journal);
        guard.Apply(10808);
        proxy.RestoreFailure = throws ? new IOException("Test proxy refused restore") : null;
        proxy.IgnoresRestore = !throws;
        var firewall = new Firewall { Enabled = true };
        var dns = new Dns();
        await using var gateway = new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"),
            KillSwitch: firewall, SystemDns: dns), clock, journal, guard);

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));

        gateway.Status.State.ShouldBe(ConnectionState.Error);
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);
        (proxy.Port, guard.IsActive, File.Exists(backup)).ShouldBe((10808, true, true));
        firewall.Enabled.ShouldBeFalse("отказ прокси не оставляет собственный firewall");
        dns.Restores.ShouldBe(1);
        proxy.RestoreFailure = null;
        proxy.IgnoresRestore = false;

        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);

        gateway.Status.State.ShouldBe(ConnectionState.Off);
        (proxy.Port, guard.IsActive, File.Exists(backup)).ShouldBe((null, false, false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disconnect_rpc_confirms_network_restore_only_when_the_block_is_released(bool keepBlock)
    {
        var firewall = new Firewall { Enabled = true };
        var block = Path.Combine(_root, "rpc-strict.json");
        BootBlock.Save(block, new HeldBlock(new KillSwitchPolicy(null, []), "example.com", Boot: true, Strict: true));
        await using var gateway = Create(firewall, new Dns(), block: block);
        var rpc = new GatewayRpc(gateway, new ServiceJournal(TimeProvider.System), new V1.ServiceInfo(), null!, null!, null!);

        var reply = await rpc.Disconnect(new V1.DisconnectRequest { KeepBlock = keepBlock }, null!);

        reply.NetworkRestored.ShouldBe(!keepBlock);
        firewall.Enabled.ShouldBe(keepBlock);
    }

    [Fact]
    public async Task Disconnect_rpc_does_not_return_confirmation_when_dns_restore_fails()
    {
        var dns = new Dns { Refuse = true };
        await using var gateway = Create(new Firewall(), dns);
        var rpc = new GatewayRpc(gateway, new ServiceJournal(TimeProvider.System), new V1.ServiceInfo(), null!, null!, null!);

        await Should.ThrowAsync<IOException>(() => rpc.Disconnect(new V1.DisconnectRequest(), null!));

        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);
        dns.Refuse = false;
    }

    private static Gateway Create(Firewall firewall, Dns dns, TimeProvider? time = null, string? block = null, BootConnection? boot = null)
    {
        var clock = time ?? TimeProvider.System;
        return new Gateway(new GatewayOptions(null, false, new Uri("https://example.com/"), KillSwitch: firewall,
            BootBlock: block, BootConnect: boot, SystemDns: dns, BlockWatch: TimeSpan.FromSeconds(1)), clock, new ServiceJournal(clock));
    }

    [Fact]
    public async Task Unreadable_firewall_is_unknown_even_when_no_block_was_known_in_memory()
    {
        var firewall = new Firewall();
        var dns = new Dns();
        await using var gateway = Create(firewall, dns);
        firewall.ReadFailure = true;

        await Should.ThrowAsync<IOException>(() => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken));
        gateway.Status.ErrorCode.ShouldBe(ErrorCodes.NetworkRestoreFailed);
        gateway.Status.State.ShouldBe(ConnectionState.Error);
        dns.Restores.ShouldBe(1);

        firewall.ReadFailure = false;
        await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
        gateway.Status.State.ShouldBe(ConnectionState.Off);
    }

    [Fact]
    public async Task Delayed_boot_key_read_cannot_reconnect_after_explicit_network_restore()
    {
        var block = Path.Combine(_root, "boot-block.json");
        BootBlock.Save(block, new HeldBlock(new KillSwitchPolicy(null, []), "example.com", Boot: true, Strict: false));
        var sealer = new DelayedSealer();
        var boot = new BootConnection(Path.Combine(_root, "boot-connection"), sealer);
        boot.Save(new PrivateGateway.Client.Contracts.V1.ConnectRequest(), [IPAddress.Parse("192.0.2.1")]);
        await using var gateway = Create(new Firewall { Enabled = true }, new Dns(), block: block, boot: boot);

        var pending = Task.Run(() => gateway.ConnectAtBootAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        try
        {
            await sealer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
            gateway.Status.State.ShouldBe(ConnectionState.Off);
        }
        finally
        {
            sealer.Continue.SetResult();
        }

        (await pending).ShouldBeFalse();
        gateway.Status.State.ShouldBe(ConnectionState.Off, "поздний результат хранилища не отменяет выход пользователя");
    }

    private sealed class DelayedSealer : ISecretSealer
    {
        public string Name => "test";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Seal(byte[] plain) => plain;
        public byte[]? Unseal(byte[] data)
        {
            Started.TrySetResult();
            Continue.Task.GetAwaiter().GetResult();
            return data;
        }
    }

    private sealed class Firewall : IKillSwitch
    {
        public string Name => "test";
        public bool Enabled { get; set; }
        public bool Refuse { get; set; }
        public bool Throws { get; set; }
        public bool ReadFailure { get; set; }
        public int Enables { get; private set; }
        public bool IsEnabled => ReadFailure ? throw new IOException("Test firewall state is unreadable") : Enabled;
        public void Enable(KillSwitchPolicy policy) { Enabled = true; Enables++; }
        public void Disable()
        {
            if (Refuse && Throws)
            {
                throw new IOException("Test firewall refused cleanup");
            }
            if (!Refuse)
            {
                Enabled = false;
            }
        }
    }

    private sealed class Dns : ISystemDns
    {
        public bool Refuse { get; set; }
        public int Restores { get; private set; }
        public void Apply(IPAddress server) { }
        public void Restore()
        {
            Restores++;
            if (Refuse)
            {
                throw new IOException("Test DNS refused restore");
            }
        }
    }

    private sealed class Sealer : ISecretSealer
    {
        public string Name => "test";
        public byte[] Seal(byte[] plain) => plain;
        public byte[]? Unseal(byte[] data) => data;
    }
}
