using System.Diagnostics;
using System.Net;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.MacOS;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Блок при обрыве на macOS: текст правил pf, как они ставятся и снимаются (с подделкой pfctl), и настоящий pfctl на
/// macOS от root — правила разных политик загружаются в свой якорь, на который основные правила машины не ссылаются:
/// синтаксис и загрузку проверяет pf, а сеть машины CI не закрывается.
/// </summary>
[Collection(FirewallSerial.Name)]
public sealed class PfKillSwitchTests : IDisposable
{
    private static readonly IPAddress Server = IPAddress.Parse("198.51.100.20");

    private readonly string _root = Directory.CreateTempSubdirectory("pg-pf-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Rules_close_everything_but_the_tunnel_the_server_and_the_network_basics()
    {
        var rules = PfKillSwitch.Ruleset(new KillSwitchPolicy("utun225", [new(Server, 443), new(IPAddress.Parse("2001:db8::20"), 8443)]));

        Lines(rules).ShouldBe([
            "pass out quick on lo0 all",
            "pass out quick on utun225 all",
            "pass out quick inet proto { tcp, udp } from any to 198.51.100.20 port 443",
            "pass out quick inet6 proto { tcp, udp } from any to 2001:db8::20 port 8443",
            "pass out quick inet proto udp from any port 68 to any port 67",
            "pass out quick inet proto udp from any port 67 to { 255.255.255.255, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } port 68",
            "pass out quick inet6 proto udp from any port 546 to any port 547",
            "pass out quick inet6 proto icmp6 all icmp6-type { routersol, neighbrsol, neighbradv }",
            "block return out quick proto tcp from any to any port { 53, 137, 138, 853, 5355 }",
            "block drop out quick proto udp from any to any port { 53, 137, 138, 853, 5355 }",
            "block return out quick proto tcp all",
            "block drop out quick all",
        ]);
    }

    [Fact]
    public void Local_network_dns_ipv6_and_the_lookup_follow_the_policy()
    {
        var lan = Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true, LanDns: IPAddress.Parse("192.168.1.53"))));
        lan.ShouldContain("pass out quick inet from any to { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }");
        lan.ShouldContain("pass out quick inet6 from any to { fc00::/7, fe80::/10 }");
        lan.IndexOf("pass out quick inet proto { tcp, udp } from any to 192.168.1.53 port 53")
            .ShouldBeLessThan(lan.IndexOf("block drop out quick proto udp from any to any port { 53, 137, 138, 853, 5355 }"), "свой DNS — раньше закрытия DNS");
        lan.IndexOf("block drop out quick proto udp from any to any port { 53, 137, 138, 853, 5355 }")
            .ShouldBeLessThan(lan.IndexOf("pass out quick inet from any to { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }"),
                "DNS роутера закрыт и при разрешенной локальной сети");
        lan.ShouldNotContain("pass out quick inet from any to { 224.0.0.0/4, 255.255.255.255 }", "обнаружение устройств — отдельной настройкой");

        var discovery = Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy(null, [], AllowLan: true, LanDiscovery: true, BlockOutsideDns: false)));
        discovery.ShouldContain("pass out quick inet from any to { 224.0.0.0/4, 255.255.255.255 }");
        discovery.ShouldContain("pass out quick inet6 from any to ff00::/8");
        discovery.ShouldNotContain(l => l.Contains("port { 53,", StringComparison.Ordinal));
        Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy(null, [], LanDiscovery: true))).ShouldNotContain(l => l.Contains("224.0.0.0/4", StringComparison.Ordinal),
            "без локальной сети обнаружения нет");

        Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy("utun225", [], Ipv6Outside: true))).ShouldContain("pass out quick inet6 all");
        Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy("utun225", []))).ShouldNotContain("pass out quick inet6 all");

        var lookup = new ServiceLookup("/opt/pg/svc", 0, [new(IPAddress.Parse("192.0.2.53"), 443)]);
        Lines(PfKillSwitch.Ruleset(new KillSwitchPolicy(null, [], Lookup: lookup)))
            .ShouldContain("pass out quick inet proto tcp from any to 192.0.2.53 port 443 user root");
    }

    [Theory]
    [InlineData("")]
    [InlineData("utun225 all\npass out quick")]
    [InlineData("utun0123456789012")]
    public void Strange_interface_names_do_not_reach_the_rules(string name) =>
        Should.Throw<ArgumentException>(() => PfKillSwitch.Ruleset(new KillSwitchPolicy(name, [])));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Ports_outside_the_range_do_not_reach_the_rules(int port) =>
        Should.Throw<ArgumentException>(() => PfKillSwitch.Ruleset(new KillSwitchPolicy("utun225", [new(Server, port)])));

    [Fact]
    public void Switch_loads_its_anchor_holds_pf_by_its_own_token_and_lets_it_go()
    {
        var calls = new List<(string Arguments, string? Input)>();
        var anchorRules = string.Empty;
        var info = "Status: Enabled for 0 days";
        var main = "scrub-anchor \"com.apple/*\" all fragment reassemble\nanchor \"com.apple/*\" all\n";
        var token = Path.Combine(_root, "run", "pf.token");
        var pf = new PfKillSwitch((arguments, input) =>
        {
            calls.Add((string.Join(' ', arguments), input));
            return string.Join(' ', arguments) switch
            {
                "-E" => (0, string.Empty, "No ALTQ support in kernel\npf enabled\nToken : 16045219372413186303"),
                "-s info" => (0, info, string.Empty),
                "-s rules" => (0, main, string.Empty),
                var a when a.EndsWith("-s rules", StringComparison.Ordinal) => (0, anchorRules, string.Empty),
                var a when a.EndsWith("-f -", StringComparison.Ordinal) => Load(input),
                var a when a.EndsWith("-F all", StringComparison.Ordinal) => Load(string.Empty),
                _ => (0, string.Empty, string.Empty),
            };
        }, token);
        (int, string, string) Load(string? rules)
        {
            anchorRules = rules ?? string.Empty;
            return (0, string.Empty, string.Empty);
        }

        pf.IsEnabled.ShouldBeFalse();
        var policy = new KillSwitchPolicy("utun225", [new(Server, 443)]);
        pf.Enable(policy);
        pf.Enable(policy with { AllowLan = true });

        calls.Where(c => c.Arguments.EndsWith("-f -", StringComparison.Ordinal)).Select(c => c.Arguments).Distinct()
            .ShouldBe([$"-a {PfKillSwitch.Anchor} -f -"], "правила — только в свой якорь под com.apple/*");
        calls.Last(c => c.Arguments.EndsWith("-f -", StringComparison.Ordinal)).Input.ShouldBe(PfKillSwitch.Ruleset(policy with { AllowLan = true }));
        calls.Count(c => c.Arguments == "-E").ShouldBe(1, "pf включается одной своей ссылкой, второй раз — только правила");
        File.ReadAllText(token).ShouldBe("16045219372413186303");
        (pf.IsEnabled, pf.IsIntact).ShouldBe((true, true));

        info = "Status: Disabled";
        pf.IsIntact.ShouldBeFalse("pf выключили — правила якоря не действуют");
        info = "Status: Enabled";
        main = "block drop all\n";
        pf.IsIntact.ShouldBeFalse("свой pf.conf без com.apple/* — якорь никто не вызывает");

        calls.Clear();
        pf.Disable();
        calls.Select(c => c.Arguments).ShouldBe([$"-a {PfKillSwitch.Anchor} -F all", "-X 16045219372413186303"], "pf отпускается своей ссылкой, а не выключается");
        File.Exists(token).ShouldBeFalse();
        pf.IsEnabled.ShouldBeFalse();
        pf.Warning.ShouldBeNull();

        pf.Enable(policy with { Direct = new DirectExit("/opt/pg/sing-box", 0) });
        pf.Warning.ShouldNotBeNull("обход sing-box на macOS по программе не узнать — пользователь должен это знать");
    }

    [Fact]
    public void A_token_left_by_a_disabled_pf_does_not_prevent_reenabling_it()
    {
        var enabled = false;
        var token = Path.Combine(_root, "pf.token");
        File.WriteAllText(token, "100");
        var calls = new List<string>();
        var pf = new PfKillSwitch((arguments, _) =>
        {
            var command = string.Join(' ', arguments);
            calls.Add(command);
            if (command == "-E")
            {
                enabled = true;
                return (0, string.Empty, "Token : 200");
            }

            return command switch
            {
                "-s info" => (0, enabled ? "Status: Enabled" : "Status: Disabled", string.Empty),
                "-s rules" => (0, "anchor \"com.apple/*\" all", string.Empty),
                var c when c.EndsWith("-s rules", StringComparison.Ordinal) => (0, "block drop out quick all", string.Empty),
                _ => (0, string.Empty, string.Empty),
            };
        }, token);

        pf.IsIntact.ShouldBeFalse();
        pf.Enable(new KillSwitchPolicy(null, []));

        pf.IsIntact.ShouldBeTrue("файл токена не заменяет фактического включения pf после чужого pfctl -d");
        File.ReadAllText(token).ShouldBe("200");
        calls.ShouldContain("-E");
        calls.IndexOf("-X 100").ShouldBeGreaterThan(calls.IndexOf("-E"), "прежнюю ссылку отпускаем только после новой");

        calls.Clear();
        pf.Enable(new KillSwitchPolicy(null, [], AllowLan: true));
        calls.ShouldNotContain("-E", "действующая ссылка не должна размножаться при каждой смене политики");
        pf.Disable();
        calls.ShouldContain("-X 200", "снимается новая ссылка, а не уже недействительный старый токен");
    }

    [Fact]
    public void Refusal_of_pfctl_is_an_error_and_not_a_silent_open_network()
    {
        var loads = 0;
        var pf = new PfKillSwitch((arguments, _) => arguments.Contains("-f") && ++loads > 0 ? (1, string.Empty, "syntax error") : (0, string.Empty, string.Empty),
            Path.Combine(_root, "pf.token"));

        Should.Throw<IOException>(() => pf.Enable(new KillSwitchPolicy(null, []))).Message.ShouldContain("syntax error");
        File.Exists(Path.Combine(_root, "pf.token")).ShouldBeFalse("правила не встали — pf не включается");
        loads.ShouldBe(1, "ошибку правил повтор не исправит — не повторять");
    }

    [Fact]
    public void Busy_pf_is_asked_again_and_stays_an_error_when_it_does_not_let_go()
    {
        var busy = 2;
        var loads = 0;
        var pf = new PfKillSwitch((arguments, _) => arguments.Contains("-f")
                ? ++loads <= busy ? (1, string.Empty, "pfctl: DIOCADDRULE: Resource busy") : (0, string.Empty, string.Empty)
                : (0, "Token : 42", string.Empty),
            Path.Combine(_root, "pf.token"));

        pf.Enable(new KillSwitchPolicy(null, []));
        loads.ShouldBe(3, "дважды занят — третья загрузка прошла");

        (busy, loads) = (int.MaxValue, 0);
        Should.Throw<IOException>(() => pf.Enable(new KillSwitchPolicy(null, []))).Message.ShouldContain("Resource busy");
        loads.ShouldBe(PfKillSwitch.BusyAttempts, "не бесконечно");
    }

    [Fact]
    public void Real_pf_loads_every_policy_into_an_anchor_and_flushes_it()
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("pf — только на macOS от root (CI)");
            return;
        }

        // Якорь не под com.apple/*: основные правила его не вызывают, загруженное не закрывает сеть машины CI.
        var anchor = "pg-test-" + Guid.NewGuid().ToString("N")[..8];
        var pf = new PfKillSwitch(Pfctl, Path.Combine(_root, "pf.token"), anchor);
        var lookup = new ServiceLookup("/opt/pg/svc", 0, [new(IPAddress.Parse("192.0.2.53"), 443)]);
        try
        {
            foreach (var policy in new KillSwitchPolicy[]
            {
                new("utun225", [new(Server, 443), new(IPAddress.Parse("2001:db8::20"), 8443)]),
                new("utun225", [new(Server, 443)], AllowLan: true, LanDiscovery: true, LanDns: IPAddress.Parse("192.168.1.53"), Lookup: lookup),
                new(null, [], AllowLan: true, BlockOutsideDns: false, Ipv6Outside: true),
            })
            {
                pf.Enable(policy);
                pf.IsEnabled.ShouldBeTrue();
                Pfctl(["-a", anchor, "-s", "rules"], null).Output.ShouldContain("block drop out quick all", customMessage: "pf принял и показывает правила якоря");
            }

            pf.Disable();
            pf.IsEnabled.ShouldBeFalse();
            File.Exists(Path.Combine(_root, "pf.token")).ShouldBeFalse();
        }
        finally
        {
            Pfctl(["-a", anchor, "-F", "all"], null);
        }
    }

    private static (int Code, string Output, string Error) Pfctl(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo("/sbin/pfctl") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(input ?? string.Empty);
        process.StandardInput.Close();
        process.WaitForExit();
        return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static List<string> Lines(string rules) => [.. rules.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
