using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Адреса TUN, оставшиеся от прошлых запусков (<see cref="WindowsTunAddresses"/>): какие снимаются — на любой ОС; снятие
/// с настоящего адаптера sing-box — на Windows от администратора (CI).
/// </summary>
[Collection(FirewallSerial.Name)]
public sealed class WindowsTunAddressesTests
{
    private static readonly IPAddress Tun = IPAddress.Parse("172.19.0.1");
    private static readonly IPAddress[] TunAddresses = [Tun, IPAddress.Parse("fdfe:dcba:9876::1")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("172.19.0.1", "Private Gateway", OperationalStatus.Up, true)]
    [InlineData("192.0.2.5", "Private Gateway", OperationalStatus.Down, true)]
    [InlineData("172.19.0.1", "Hiddify", OperationalStatus.Down, true)]
    [InlineData("172.19.0.1", null, null, true)]
    [InlineData("fdfe:dcba:9876::1", "Wintun Userspace Tunnel", OperationalStatus.NotPresent, true)]
    [InlineData("172.19.0.1", "vEthernet (Default Switch)", OperationalStatus.Up, false)]
    [InlineData("192.0.2.5", "Ethernet", OperationalStatus.Down, false)]
    [InlineData("192.0.2.5", null, null, false)]
    public void Which_addresses_are_stale(string address, string? owner, OperationalStatus? status, bool stale) =>
        WindowsTunAddresses.IsStale(IPAddress.Parse(address), owner, status, "Private Gateway", TunAddresses).ShouldBe(stale);

    [Fact]
    public async Task Addresses_of_the_own_adapter_are_removed()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("TUN Windows — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipWhen(XrayKit.SingBox is null, "нет sing-box — scripts/get-singbox.sh");

        // Адаптер «Private Gateway» с адресом TUN — как остается от прошлого запуска: sing-box без Xray (выход к нему при запуске
        // не нужен), в TUN — только адрес документации, без строгих маршрутов: машина CI остается в сети.
        var subnet = TunSubnet.Choose(VpnTunnel.MachineNetworks());
        var tun = SingBoxConfigBuilder.BuildTun(new TunRequest(VpnTunnel.NewLink(IPAddress.Loopback), [IPAddress.Loopback], ProtectionSettings.Default,
            ConnectionSettings.Default, new TunIpv6(Kernel: true, Tunnel: false), Routes: ["192.0.2.123/32"], Subnet: subnet));
        var output = new CoreOutput();
        var started = 0;
        using var singBox = CoreProcess.StartSingBox(XrayKit.SingBox!, tun, output: line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started, 1);
            }

            output.Add("sing-box", line);
        });
        try
        {
            await WaitAsync(() => Volatile.Read(ref started) == 1 && Addresses().Contains(subnet.Address), "адаптер с адресом TUN", output);

            var removed = WindowsTunAddresses.RemoveStale(SingBoxConfigBuilder.InterfaceName, [subnet.Address]);

            removed.ShouldContain(r => r.StartsWith($"{subnet.Address}/30 ({SingBoxConfigBuilder.InterfaceName})", StringComparison.Ordinal), string.Join("; ", removed));
            removed.ShouldNotContain(r => r.Contains("не снят", StringComparison.Ordinal));
            await WaitAsync(() => !Addresses().Contains(subnet.Address), "адрес TUN снят с адаптера", output);
        }
        finally
        {
            await singBox.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private static IPAddress[] Addresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces().Where(n => n.Name == SingBoxConfigBuilder.InterfaceName)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address),
    ];

    private static async Task WaitAsync(Func<bool> ready, string what, CoreOutput output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            if (DateTime.UtcNow > deadline)
            {
                await output.TraceAsync<bool>(() => throw new InvalidOperationException("Не дождались: " + what));
            }

            await Task.Delay(100, Ct);
        }
    }
}
