using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Блок при обрыве на Windows: правила WFP — точным списком на любой ОС, сами фильтры — на Windows с
/// правами администратора (в CI) только для одной программы, curl: машина CI остается в сети.
/// Локальную сеть и DNS роутера проверяет только список правил: другой машины в частной сети у
/// машины CI нет, а соединение со своим адресом Windows считает петлей.
/// </summary>
[Collection(WfpSerial.Name)]
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
            "15 permit V4 dhcp-server: Protocol 17, LocalPort 67, RemotePort 68, RemoteAddress 0.0.0.0/32, RemoteAddress 255.255.255.255/32, " +
                "RemoteAddress 10.0.0.0/8, RemoteAddress 172.16.0.0/12, RemoteAddress 192.168.0.0/16, RemoteAddress 169.254.0.0/16",
            "15 permit V6 dhcp: Protocol 17, LocalPort 546, RemotePort 547",
            "15 permit V6 neighbors: Protocol 58, IcmpType 133, IcmpType 134, IcmpType 135, IcmpType 136",
            "10 block V4 dns: RemotePort 53, RemotePort 137, RemotePort 138, RemotePort 853, RemotePort 5355, Protocol 6, Protocol 17",
            "0 block V4 block: ",
            "10 block V6 dns: RemotePort 53, RemotePort 137, RemotePort 138, RemotePort 853, RemotePort 5355, Protocol 6, Protocol 17",
            "0 block V6 block: ",
        ], "решает подошедший фильтр с наибольшим весом; локальная сеть по умолчанию закрыта; адаптер туннеля есть — " +
            "пересылку ведут маршруты в туннель, фильтров пересылки нет");
    }

    [Fact]
    public void Forwarding_without_the_tunnel_is_closed_but_the_allowed_local_network()
    {
        WfpRules.For(new KillSwitchPolicy("Private Gateway", [new(Server, 443)]), tunnelLuid: null)
            .Where(r => r.Hook == WfpHook.Forward).Select(r => r.ToString()).ShouldBe([
                "15 permit V4 forward pass-through: Flags 262144, Flags 524288",
                "0 block V4 forward block: ",
                "15 permit V6 forward pass-through: Flags 262144, Flags 524288",
                "0 block V6 forward block: ",
            ], "туннеля нет — пересылка (хот-спот, ВМ, контейнеры с NAT) закрыта; пакеты самой машины решают слои соединений");

        var lan = WfpRules.For(new KillSwitchPolicy("Private Gateway", [new(Server, 443)], AllowLan: true, LanDiscovery: true,
            Direct: new DirectExit(@"C:\pg\sing-box.exe", 0x5047)), tunnelLuid: null).Where(r => r.Hook == WfpHook.Forward).ToList();
        lan.Select(r => r.ToString()).ShouldContain(
            "5 permit V4 forward lan: DestinationAddress 10.0.0.0/8, DestinationAddress 172.16.0.0/12, DestinationAddress 192.168.0.0/16, " +
            "DestinationAddress 169.254.0.0/16", "локальная сеть разрешена — по адресу назначения в обе стороны: и ответ клиенту хот-спота");
        lan.Select(r => r.ToString()).ShouldContain("5 permit V6 forward lan: DestinationAddress fc00::/7, DestinationAddress fe80::/10");
        lan.Select(r => r.ToString()).ShouldContain("5 permit V4 forward discovery: DestinationAddress 224.0.0.0/4, DestinationAddress 255.255.255.255/32");
        lan.ShouldNotContain(r => r.Name == "server" || r.Name == "dns" || r.Name == "direct",
            "на слое пересылки нет портов и программ: сервер нужен только службе, DNS и обход — программам машины");
        WfpRules.For(new KillSwitchPolicy(null, [], AllowLan: true), tunnelLuid: 42).ShouldNotContain(r => r.Hook == WfpHook.Forward);
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
        WfpRules.For(new KillSwitchPolicy("Private Gateway", [], Ipv6Outside: true), 7).Select(r => r.ToString())
            .ShouldContain("5 permit V6 ipv6: ", "«Блокировать IPv6 вне туннеля» выключено при выключенном блоке — IPv6 мимо туннеля открыт, ниже закрытия DNS");
        lan.ShouldNotContain(r => r.Contains(" ipv6:", StringComparison.Ordinal));
        WfpRules.For(new KillSwitchPolicy(null, [], AllowLan: true), null)
            .ShouldNotContain(r => r.Name == "discovery", "обнаружение устройств — отдельной настройкой");
        Should.Throw<ArgumentException>(() => WfpRules.For(new KillSwitchPolicy(null, [new(Server, 0)]), null));
        Should.Throw<ArgumentException>(() => WfpRules.For(
            new KillSwitchPolicy(null, [.. Enumerable.Range(1, WfpRules.MaxServers + 1).Select(i => new KillSwitchEndpoint(Server, i))]), null));
    }

    [Fact]
    public void Split_mode_exit_is_the_sing_box_program_above_the_dns_rule()
    {
        var rules = WfpRules.For(new KillSwitchPolicy("Private Gateway", [], Direct: new DirectExit(@"C:\Program Files\Private Gateway\sing-box.exe", 0x5047)), 42)
            .Select(r => r.ToString()).ToList();

        rules.ShouldContain(@"15 permit V4 direct: App C:\Program Files\Private Gateway\sing-box.exe");
        rules.ShouldContain(@"15 permit V6 direct: App C:\Program Files\Private Gateway\sing-box.exe", "вес выше закрытия DNS (10): обход выбран пользователем");
        WfpRules.For(new KillSwitchPolicy("Private Gateway", []), 42).ShouldNotContain(r => r.Name == "direct");
    }

    [Fact]
    public void Own_dns_in_a_private_network_is_permitted_above_the_dns_rule()
    {
        WfpRules.For(new KillSwitchPolicy("Private Gateway", [], LanDns: IPAddress.Parse("192.168.1.53")), 42).Select(r => r.ToString())
            .ShouldContain("15 permit V4 lan-dns: RemoteAddress 192.168.1.53, RemotePort 53, Protocol 6, Protocol 17", "вес выше закрытия DNS (10)");
        WfpRules.For(new KillSwitchPolicy("Private Gateway", [], LanDns: IPAddress.Parse("fd00::53")), 42).Select(r => r.ToString())
            .ShouldContain("15 permit V6 lan-dns: RemoteAddress fd00::53, RemotePort 53, Protocol 6, Protocol 17");
        WfpRules.For(new KillSwitchPolicy("Private Gateway", []), 42).ShouldNotContain(r => r.Name == "lan-dns");
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
        // Свой адрес Windows считает петлей — поэтому «внешний» сервер настоящий: github.com, доступный с машин CI.
        // Имя разрешает тест, curl идет по адресу (--resolve): DNS ему не нужен.
        var github = (await Dns.GetHostAddressesAsync("github.com", AddressFamily.InterNetwork, TestContext.Current.CancellationToken)).FirstOrDefault();
        Assert.SkipWhen(github is null, "github.com не разрешился");
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.GetIPProperties().GatewayAddresses.Count > 0)?.Name;
        using var loopback = new LocalSite("петля");
        var killSwitch = new WfpKillSwitch(onlyProgram: curl);
        try
        {
            (await Reaches(curl, github, 443)).ShouldBeTrue("без блока сеть открыта");

            killSwitch.Enable(new KillSwitchPolicy(null, [new(github, 443)]));

            killSwitch.IsEnabled.ShouldBeTrue();
            (await Reaches(curl, github, 443)).ShouldBeTrue("к серверу — мимо туннеля");
            (await Reaches(curl, github, 80)).ShouldBeFalse("другой порт того же адреса — не сервер");
            (await Run(curl, "-sS", "-m", "5", loopback.Url.ToString())).ShouldBe("петля");
            WfpKillSwitch.IsPersistent.ShouldBeFalse("без «Защиты при загрузке» фильтры уходят с перезагрузкой");

            killSwitch.Enable(new KillSwitchPolicy(null, [new(github, 443)], Persistent: true));

            WfpKillSwitch.IsPersistent.ShouldBeTrue("«Защита при загрузке»: служба фильтрации поставит их и после перезагрузки, до старта службы");
            (await Reaches(curl, github, 443)).ShouldBeTrue();
            (await Reaches(curl, github, 80)).ShouldBeFalse("постоянные фильтры закрывают так же");

            killSwitch.Enable(new KillSwitchPolicy(null, []));

            (await Reaches(curl, github, 443)).ShouldBeFalse("сервера в правилах больше нет — закрыто все");

            killSwitch.Enable(new KillSwitchPolicy(null, [], Direct: new DirectExit(curl, 0x5047)));

            (await Reaches(curl, github, 80)).ShouldBeTrue("программа выхода мимо туннеля (в службе — sing-box) проходит куда угодно");

            if (adapter is not null)
            {
                killSwitch.Enable(new KillSwitchPolicy(adapter, []));

                (await Reaches(curl, github, 443)).ShouldBeTrue("через адаптер «туннеля» — все");
            }

            killSwitch.Disable();
            killSwitch.Disable();

            killSwitch.IsEnabled.ShouldBeFalse();
            (await Reaches(curl, github, 80)).ShouldBeTrue("блок снят — сеть открыта");
        }
        finally
        {
            killSwitch.Disable();
        }
    }

    [Fact]
    public void Hyper_v_rules_close_the_vms_and_go_away_with_the_block()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("брандмауэр Hyper-V — только на Windows с правами администратора (CI)");
            return;
        }

        Assert.SkipUnless(HyperVFirewall.Available(), "нет брандмауэра Hyper-V на этой Windows");
        try
        {
            HyperVFirewall.Close().ShouldBeNull();
            HyperVFirewall.IsClosed().ShouldBeTrue("оба правила стоят\n" + HyperVRules());
            HyperVFirewall.Close().ShouldBeNull("повторно — заменяет, не падает");

            WfpKillSwitch.ForService().Warning.ShouldBeNull();
            HyperVFirewall.IsClosed().ShouldBeFalse("фильтров блока нет — правила Hyper-V, оставшиеся с прошлого раза, снимаются при запуске службы");

            HyperVFirewall.Close().ShouldBeNull();
            HyperVFirewall.Open().ShouldBeNull();
            HyperVFirewall.IsClosed().ShouldBeFalse();
            HyperVFirewall.Open().ShouldBeNull("правил нет — снять их не ошибка");
        }
        finally
        {
            HyperVFirewall.Open();
        }
    }

    /// <summary>Что видно в классе правил Hyper-V без хранилища и в каждом из них — для сообщения теста.</summary>
    [SupportedOSPlatform("windows")]
    private static string HyperVRules()
    {
        var lines = new List<string>();
        foreach (var store in new[] { null, "PersistentStore", "ActiveStore" })
        {
            try
            {
                var scope = new ManagementScope(HyperVFirewall.Namespace);
                scope.Connect();
                var options = new System.Management.EnumerationOptions { Context = store is null ? null : new ManagementNamedValueCollection { { "PolicyStore", store } } };
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + HyperVFirewall.Class), options);
                var all = searcher.Get().Cast<ManagementBaseObject>().Select(o => $"{o["InstanceID"]} «{o["ElementName"]}»").ToList();
                lines.Add($"{store ?? "без хранилища"}: {all.Count} — {string.Join("; ", all.Take(6))}");
            }
            catch (Exception e) when (e is ManagementException or COMException)
            {
                lines.Add($"{store ?? "без хранилища"}: ошибка {e.Message}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Проба: закрывают ли фильтры пересылки контейнер Windows за NAT (WinNAT — как у Docker, WSL и ВМ на «Default Switch»).
    /// Источники расходятся: по Mullvad, трафик WSL за NAT мимо слоев IP WFP. Только в CI (PG_FORWARD_PROBE=1): тянет образ
    /// nanoserver. Фильтры соединений — только для копии curl, машина CI остается в сети; пересылка закрывается на всю
    /// машину, ее у машины CI нет. Итог — в artifacts/forward-probe.txt, CI выводит его в журнал.
    /// </summary>
    [Fact]
    public async Task Probe_container_behind_nat_and_the_forward_filters()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("PG_FORWARD_PROBE") == "1", "проба пересылки — только в CI (PG_FORWARD_PROBE=1)");
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("фильтры WFP ставятся только на Windows с правами администратора (CI)");
            return;
        }

        var tag = Environment.OSVersion.Version.Build switch
        {
            >= 26100 => "ltsc2025",
            >= 20348 => "ltsc2022",
            _ => null,
        };
        Assert.SkipWhen(tag is null, "нет образа nanoserver под эту сборку Windows");
        var image = $"mcr.microsoft.com/windows/nanoserver:{tag}";
        var github = (await Dns.GetHostAddressesAsync("github.com", AddressFamily.InterNetwork, TestContext.Current.CancellationToken)).FirstOrDefault();
        Assert.SkipWhen(github is null, "github.com не разрешился");
        var curl = Path.Combine(Path.GetTempPath(), "pg-probe-curl.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "curl.exe"), curl, overwrite: true);
        var report = new List<string> { $"образ {image}, github.com {github}" };
        var killSwitch = new WfpKillSwitch(onlyProgram: curl);
        try
        {
            var open = await Container();
            report.Add("без блока: " + (open ? "контейнер проходит" : "контейнер не проходит"));
            Assert.SkipUnless(open, "контейнер не выходит в сеть и без блока — проверять нечего");

            killSwitch.Enable(new KillSwitchPolicy(null, [new(github, 443)]));
            report.Add("блок без туннеля (пересылка закрыта): " + (await Container() ? "контейнер ПРОХОДИТ — пересылку WinNAT фильтры не видят" : "контейнер закрыт"));
            report.Add("машина (копия curl, сервер в правилах): " + (await Reaches(curl, github, 443) ? "проходит" : "не проходит"));

            killSwitch.Disable();
            report.Add("после снятия: " + (await Container() ? "контейнер проходит" : "контейнер НЕ ПРОХОДИТ — фильтры остались или сеть пропала"));
        }
        finally
        {
            killSwitch.Disable();
            File.WriteAllLines(Path.Combine(RepositoryRoot(), "artifacts", "forward-probe.txt"), report);
        }

        async Task<bool> Container() => await Docker("run", "--rm", "--isolation=process", image, "curl.exe", "-sS", "-o", "NUL",
            "--connect-timeout", "5", "-m", "10", "--resolve", $"github.com:443:{github}", "https://github.com/") is not null;
    }

    /// <summary>docker с аргументами; не вышел успешно за 3 минуты — null; docker нет — тест пропускается.</summary>
    private static async Task<string?> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? started = null;
        try
        {
            started = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("нет docker");
        }

        using var process = started!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        _ = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        return process.ExitCode == 0 ? await output : null;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx)");
    }

    /// <summary>Соединяется ли curl с github.com по адресу и порту (ответ HTTP любой).</summary>
    private static async Task<bool> Reaches(string curl, IPAddress address, int port) =>
        await Run(curl, "-sS", "-o", "NUL", "--connect-timeout", "5", "-m", "10", "--resolve", $"github.com:{port}:{address}",
            $"{(port == 443 ? "https" : "http")}://github.com:{port}/") is not null;

    /// <summary>Вывод curl.exe; не соединился (блок) — null.</summary>
    private static async Task<string?> Run(string curl, params string[] arguments)
    {
        var start = new ProcessStartInfo(curl) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return process.ExitCode == 0 ? output : null;
    }
}
