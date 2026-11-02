using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Реальный Xray уже слушает локальный вход, но проверка IPv6 удерживает запуск до создания TUN. Остановка должна
/// отменить эту часть, дождаться ее очистки и закрыть ядро, прежде чем вернуть управление или начать следующую сессию.
/// Firewall поддельный; вместо sing-box указан Xray: даже при регрессии тест не может создать системный TUN.
/// </summary>
[Collection(NamespaceSerial.Name)]
public sealed class GatewayStartupCancellationTests
{
    [Theory]
    [InlineData("disconnect")]
    [InlineData("pause")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public async Task Changing_the_session_waits_for_unpublished_startup_cleanup(string change)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        // Запуск удерживает проверка IPv6 сервера; без IPv6 в ядре ОС (контейнер облачной копии) ее нет, держать нечем.
        Assert.SkipUnless(VpnTunnel.AdapterIpv6(), "нет IPv6 в ядре ОС — проверки IPv6 сервера перед TUN нет");
        var gate = new StartupGate();
        var time = TimeProvider.System;
        var firewall = new FakeFirewall();
        var journal = new ServiceJournal(time);
        var gateway = new Gateway(new GatewayOptions(XrayKit.Xray, CanVpn: true, new Uri("https://example.com/"),
            SingBoxPath: XrayKit.Xray, KillSwitch: firewall, Trace: new Uri("https://example.com/trace"),
            Trace6: new Uri("https://example.com/trace6"), ExitIp: gate.ReadAsync, TrafficWatch: Timeout.InfiniteTimeSpan), time, journal);
        var disposed = false;
        Task? changing = null;
        try
        {
            (await gateway.ConnectAsync(Request("old"), TestContext.Current.CancellationToken)).Accepted.ShouldBeTrue();
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            gateway.Status.State.ShouldBe(ConnectionState.Connecting);
            await AssertListensAsync(gate.Port, expected: true);

            changing = change switch
            {
                "disconnect" => gateway.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken),
                "pause" => gateway.PauseAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken),
                "replace" => gateway.ConnectAsync(Request("new", proxy: true), TestContext.Current.CancellationToken),
                "dispose" => gateway.DisposeAsync().AsTask(),
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };

            await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            changing.IsCompleted.ShouldBeFalse("очистка еще удерживается: команда не может объявить отключение завершенным");
            gate.ReleaseCleanup.TrySetResult();
            await changing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            disposed = change == "dispose";

            gate.Readers.ShouldBe(0, "команда дожидается обеих проверок перед возвратом");
            await AssertListensAsync(gate.Port, expected: false);
            journal.Tail(ServiceJournal.Capacity).ShouldNotContain(e => e.Source == "sing-box",
                "после отмены ядра TUN не запускаются даже на мгновение");
            if (change == "disconnect")
            {
                gateway.Status.State.ShouldBe(ConnectionState.Off);
                firewall.IsEnabled.ShouldBeFalse();
            }
            else if (change == "pause")
            {
                gateway.Status.IsPaused.ShouldBeTrue();
                firewall.IsEnabled.ShouldBeFalse();
            }
            else if (change == "replace")
            {
                gateway.Status.ServerId.ShouldBe("new", "отмена старой попытки не повреждает новую сессию");
                gateway.Status.State.ShouldBeOneOf(ConnectionState.Connecting, ConnectionState.Connected);
            }
        }
        finally
        {
            gate.ReleaseCleanup.TrySetResult();
            if (changing is not null)
            {
                await changing.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                disposed |= change == "dispose";
            }
            if (!disposed)
            {
                await gateway.DisposeAsync();
            }
        }
    }

    private static async Task AssertListensAsync(int port, bool expected)
    {
        using var client = new TcpClient();
        bool listening;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            listening = true;
        }
        catch (SocketException)
        {
            listening = false;
        }
        listening.ShouldBe(expected, "вход реального старого Xray должен закрыться до завершения команды");
    }

    private static V1.ConnectRequest Request(string id, bool proxy = false)
    {
        var definition = new ServerDefinition("test", string.Empty, ProxyProtocol.Vless, "198.51.100.20", 443,
            TransportOptions.Tcp, new SecurityOptions(TransportSecurity.Tls, Sni: "example.com"), ProtocolOptions.Empty,
            new ServerCredentials(Uuid: XrayKit.Uuid));
        var settings = AppSettings.Default with
        {
            Protection = ProtectionSettings.Default with { CheckLeaksAfterConnect = false, BootProtection = false },
            Connection = ConnectionSettings.Default with { ConnectTimeoutSeconds = 30, ProxyPort = XrayKit.FreePort() },
        };
        return new V1.ConnectRequest
        {
            Server = definition.ToContract(id), Mode = proxy ? V1.Mode.Proxy : V1.Mode.Vpn,
            Protection = settings.ToContract(), Connection = settings.ToConnection(), Routing = settings.ToRouting(),
        };
    }

    private sealed class StartupGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;
        public int Readers => Volatile.Read(ref _readers);
        public int Port { get; private set; }

        public async Task<string?> ReadAsync(ServerProbe probe, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readers);
            Port = probe.Port;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
            catch (OperationCanceledException e)
            {
                Cancelled.TrySetResult();
                await ReleaseCleanup.Task;
                // Не позволяем внутреннему таймауту пробы перейти к настоящему TUN даже на сломанной реализации.
                throw new IOException("Отменена управляемая проверка запуска", e);
            }
            finally
            {
                Interlocked.Decrement(ref _readers);
            }
        }
    }

    private sealed class FakeFirewall : IKillSwitch
    {
        public string Name => "fake";
        public bool IsEnabled { get; private set; }
        public bool IsIntact => IsEnabled;
        public void Enable(KillSwitchPolicy policy) => IsEnabled = true;
        public void Disable() => IsEnabled = false;
    }
}
