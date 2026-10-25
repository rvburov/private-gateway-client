using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Блок при обрыве на Windows: правила WFP — точным списком на любой ОС, сами фильтры — на Windows с
/// правами администратора (в CI) только для одной программы, curl: машина CI остается в сети.
/// </summary>
public sealed class WfpKillSwitchTests
{
    private static readonly IPAddress Server = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void Rules_close_everything_but_the_tunnel_the_server_and_the_network_basics()
    {
        var rules = WfpRules.For(new KillSwitchPolicy("Private Gateway", [new(Server, 443), new(IPAddress.Parse("2001:db8::20"), 8443)]), tunnelLuid: 42)
            .Select(r => r.ToString());

        rules.ShouldBe([
            "15 permit V4 loopback: Flags 1",
            "15 permit V4 tunnel: LocalInterface 42",
            "15 permit V6 loopback: Flags 1",
            "15 permit V6 tunnel: LocalInterface 42",
            "15 permit V4 server: RemoteAddress 198.51.100.20, RemotePort 443, Protocol 6, Protocol 17",
            "15 permit V6 server: RemoteAddress 2001:db8::20, RemotePort 8443, Protocol 6, Protocol 17",
            "15 permit V4 dhcp: Protocol 17, LocalPort 68, RemotePort 67",
            "15 permit V6 dhcp: Protocol 17, LocalPort 546, RemotePort 547",
            "15 permit V6 neighbors: Protocol 58, IcmpType 133, IcmpType 134, IcmpType 135, IcmpType 136",
            "10 block V4 dns: RemotePort 53, RemotePort 853, Protocol 6, Protocol 17",
            "0 block V4 block: ",
            "10 block V6 dns: RemotePort 53, RemotePort 853, Protocol 6, Protocol 17",
            "0 block V6 block: ",
        ], "решает подошедший фильтр с наибольшим весом; локальная сеть по умолчанию закрыта");
    }

    [Fact]
    public void Local_network_follows_the_settings_and_the_tunnel_waits_for_its_adapter()
    {
        var lan = WfpRules.For(new KillSwitchPolicy("Private Gateway", [], AllowLan: true, LanDiscovery: true, BlockOutsideDns: false), tunnelLuid: null)
            .Select(r => r.ToString()).ToList();

        lan.ShouldContain("5 permit V4 lan: RemoteAddress 10.0.0.0/8, RemoteAddress 172.16.0.0/12, RemoteAddress 192.168.0.0/16, RemoteAddress 169.254.0.0/16");
        lan.ShouldContain("5 permit V6 lan: RemoteAddress fc00::/7, RemoteAddress fe80::/10");
        lan.ShouldContain("5 permit V4 discovery: RemoteAddress 224.0.0.0/4, RemoteAddress 255.255.255.255/32");
        lan.ShouldContain("5 permit V6 discovery: RemoteAddress ff00::/8");
        lan.ShouldNotContain(r => r.Contains("tunnel", StringComparison.Ordinal), "адаптера еще нет — разрешение добавится, когда он появится");
        lan.ShouldNotContain(r => r.Contains(" dns:", StringComparison.Ordinal));

        WfpRules.For(new KillSwitchPolicy(null, [], LanDiscovery: true), null)
            .ShouldNotContain(r => r.Name == "discovery", "без локальной сети нет и обнаружения устройств");
        WfpRules.For(new KillSwitchPolicy(null, [], AllowLan: true), null)
            .ShouldNotContain(r => r.Name == "discovery", "обнаружение устройств — отдельной настройкой");
        Should.Throw<ArgumentException>(() => WfpRules.For(new KillSwitchPolicy(null, [new(Server, 0)]), null));
        Should.Throw<ArgumentException>(() => WfpRules.For(
            new KillSwitchPolicy(null, [.. Enumerable.Range(1, WfpRules.MaxServers + 1).Select(i => new KillSwitchEndpoint(Server, i))]), null));
    }

    [Fact]
    public void Filter_keys_go_in_order_from_one_base()
    {
        WfpRules.FilterKey(0).ShouldBe(new Guid("c9f98d15-2e6c-4db0-aa5c-48a623d10000"));
        WfpRules.FilterKey(258).ShouldBe(new Guid("c9f98d15-2e6c-4db0-aa5c-48a623d10102"), "номер — в последних двух байтах");
    }

    [Fact]
    public async Task Filters_let_curl_through_only_where_the_policy_allows()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("фильтры WFP ставятся только на Windows с правами администратора (CI)");
            return;
        }

        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        Assert.SkipUnless(File.Exists(curl), "нет curl.exe");
        var (host, adapter) = PrivateAddress();
        Assert.SkipWhen(host is null, "у машины нет адреса в частной сети");
        using var server = new LocalSite("сервер", host);
        using var lan = new LocalSite("локальная сеть", host);
        using var dns = new LocalSite("dns", host, 53);
        using var loopback = new LocalSite("петля");
        var killSwitch = new WfpKillSwitch(onlyProgram: curl);
        try
        {
            (await Curl(curl, lan.Url)).ShouldBe("локальная сеть", "без блока сеть открыта");

            killSwitch.Enable(new KillSwitchPolicy(null, [new(host, server.Url.Port)]));

            killSwitch.IsEnabled.ShouldBeTrue();
            (await Curl(curl, server.Url)).ShouldBe("сервер", "к серверу — мимо туннеля");
            (await Curl(curl, loopback.Url)).ShouldBe("петля");
            (await Curl(curl, lan.Url)).ShouldBeNull("другой порт того же адреса — не сервер");

            killSwitch.Enable(new KillSwitchPolicy(null, [], AllowLan: true));

            (await Curl(curl, lan.Url)).ShouldBe("локальная сеть", "локальная сеть разрешена");
            (await Curl(curl, dns.Url)).ShouldBeNull("DNS роутера мимо туннеля закрыт и при разрешенной локальной сети");

            killSwitch.Enable(new KillSwitchPolicy(null, [], AllowLan: true, BlockOutsideDns: false));

            (await Curl(curl, dns.Url)).ShouldBe("dns");

            killSwitch.Enable(new KillSwitchPolicy(adapter, []));

            (await Curl(curl, lan.Url)).ShouldBe("локальная сеть", "через адаптер туннеля — все");

            killSwitch.Disable();
            killSwitch.Disable();

            killSwitch.IsEnabled.ShouldBeFalse();
            (await Curl(curl, lan.Url)).ShouldBe("локальная сеть");
        }
        finally
        {
            killSwitch.Disable();
        }
    }

    /// <summary>Адрес машины в частной сети IPv4 и имя его адаптера.</summary>
    private static (IPAddress? Address, string? Adapter) PrivateAddress()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                var bytes = unicast.Address.GetAddressBytes();
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)))
                {
                    return (unicast.Address, adapter.Name);
                }
            }
        }

        return (null, null);
    }

    /// <summary>Страница через curl.exe; соединение не вышло (блок) — null.</summary>
    private static async Task<string?> Curl(string curl, Uri url)
    {
        var start = new ProcessStartInfo(curl) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-sS", "-m", "5", url.ToString() })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0 ? output : null;
    }
}
