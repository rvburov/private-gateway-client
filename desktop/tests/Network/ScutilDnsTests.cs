using System.Diagnostics;
using System.Net;
using PrivateGateway.Client.Platform.MacOS;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// DNS macOS на время туннеля (<see cref="ScutilDns"/>): с подделкой динамического хранилища configd — что подменяется,
/// что остается, что возвращается и после смены основной сети; и настоящий scutil на macOS от root — на своей записи
/// сети, которой нет в порядке служб: DNS машины CI не меняется.
/// </summary>
public sealed class ScutilDnsTests : IDisposable
{
    private static readonly IPAddress Tunnel = IPAddress.Parse("172.19.0.2");

    private readonly string _root = Directory.CreateTempSubdirectory("pg-dns-").FullName;
    private readonly FakeStore _store = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Backup => Path.Combine(_root, "dns-backup.json");

    [Fact]
    public void Primary_network_dns_points_at_the_tunnel_and_comes_back_with_its_search_domains()
    {
        _store.Primary = "6A8B-11";
        _store.Keys["State:/Network/Service/6A8B-11/DNS"] = new() { ["ServerAddresses"] = ["192.168.1.1", "fe80::1%en0"], ["SearchDomains"] = ["example.org"] };
        var dns = new ScutilDns(_store.Run, Backup);

        dns.Apply(Tunnel);
        dns.Apply(Tunnel);

        _store.Keys["State:/Network/Service/6A8B-11/DNS"]["ServerAddresses"].ShouldBe(["172.19.0.2"]);
        _store.Keys["State:/Network/Service/6A8B-11/DNS"]["SearchDomains"].ShouldBe(["example.org"], "домены поиска остаются");
        _store.Keys.ShouldNotContainKey("Setup:/Network/Service/6A8B-11/DNS", "DNS вручную не задан — и не появляется");
        File.Exists(Backup).ShouldBeTrue("копия прежнего — на случай аварии службы");

        dns.Restore();

        _store.Keys["State:/Network/Service/6A8B-11/DNS"]["ServerAddresses"].ShouldBe(["192.168.1.1", "fe80::1%en0"], "второй Apply копию не затер");
        _store.Keys["State:/Network/Service/6A8B-11/DNS"]["SearchDomains"].ShouldBe(["example.org"]);
        File.Exists(Backup).ShouldBeFalse();
        dns.Restore();
        _store.Scripts.Count(s => s.Contains("set ", StringComparison.Ordinal)).ShouldBe(3, "возвращать нечего — ничего не меняется");
    }

    [Fact]
    public void Manual_dns_is_overridden_too_and_a_new_primary_network_gets_the_tunnel_while_the_old_one_gets_its_dns_back()
    {
        _store.Primary = "WIFI";
        _store.Keys["State:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["192.168.1.1"] };
        _store.Keys["Setup:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["192.0.2.53"] };
        var dns = new ScutilDns(_store.Run, Backup);

        dns.Apply(Tunnel);
        _store.Keys["Setup:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["172.19.0.2"], "заданный вручную DNS важнее выданного DHCP");

        _store.Primary = "ETHERNET";
        dns.Apply(Tunnel);

        _store.Keys["State:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.168.1.1"], "прежней сети — ее DNS");
        _store.Keys["Setup:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.0.2.53"]);
        _store.Keys["State:/Network/Service/ETHERNET/DNS"]["ServerAddresses"].ShouldBe(["172.19.0.2"]);

        dns.Restore();
        _store.Keys.ShouldNotContainKey("State:/Network/Service/ETHERNET/DNS", "записи не было — ее и нет");
    }

    [Fact]
    public void Strange_answers_of_the_os_do_not_reach_the_commands()
    {
        _store.Primary = "6A8B remove State:/Network/Global/DNS";
        Should.Throw<IOException>(() => new ScutilDns(_store.Run, Backup).Apply(Tunnel));

        _store.Primary = null;
        Should.Throw<IOException>(() => new ScutilDns(_store.Run, Backup).Apply(Tunnel)).Message.ShouldContain("основной сети");

        _store.Primary = "WIFI";
        _store.Keys["State:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["192.168.1.1; remove x"] };
        var dns = new ScutilDns(_store.Run, Backup);
        dns.Apply(Tunnel);
        Should.Throw<IOException>(dns.Restore, "непонятный прежний адрес в команду не попадает");
    }

    [Fact]
    public void Scutil_output_is_read_as_scutil_prints_it()
    {
        ScutilDns.ParsePrimary("""
            <dictionary> {
              PrimaryInterface : en0
              PrimaryService : 6A8B1F2C-1111-2222-3333-444455556666
              Router : 192.168.1.1
            }
            """).ShouldBe("6A8B1F2C-1111-2222-3333-444455556666");
        ScutilDns.ParsePrimary("  No such key\n").ShouldBeNull();
        ScutilDns.ParseServers("""
            <dictionary> {
              SearchDomains : <array> {
                0 : example.org
              }
              ServerAddresses : <array> {
                0 : 192.168.1.1
                1 : fe80::1%en0
              }
            }
            """).ShouldBe(["192.168.1.1", "fe80::1%en0"]);
        ScutilDns.ParseServers("<dictionary> {\n  DomainName : example.org\n}\n").ShouldBe([], "запись без серверов");
        ScutilDns.ParseServers("  No such key\n").ShouldBeNull();
    }

    [Fact]
    public void Real_scutil_replaces_and_returns_the_servers_of_a_network()
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("scutil — только на macOS от root (CI)");
            return;
        }

        // Своя запись сети — ее нет в порядке служб configd, DNS машины CI не меняется.
        var service = "pg-test-" + Guid.NewGuid().ToString("N")[..8];
        var key = $"State:/Network/Service/{service}/DNS";
        Scutil($"d.init\nd.add ServerAddresses * 192.0.2.53\nd.add SearchDomains * example.org\nset {key}\nquit\n");
        try
        {
            var dns = new ScutilDns(Scutil, Backup, () => service);
            dns.Apply(Tunnel);
            var applied = Scutil($"show {key}\nquit\n");
            ScutilDns.ParseServers(applied).ShouldBe(["172.19.0.2"]);
            applied.ShouldContain("example.org");

            dns.Restore();
            ScutilDns.ParseServers(Scutil($"show {key}\nquit\n")).ShouldBe(["192.0.2.53"]);
        }
        finally
        {
            Scutil($"remove {key}\nquit\n");
        }
    }

    private static string Scutil(string commands)
    {
        var start = new ProcessStartInfo("/usr/sbin/scutil") { RedirectStandardInput = true, RedirectStandardOutput = true };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(commands);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    /// <summary>Динамическое хранилище configd в памяти: команды scutil show, get, d.init, d.add, d.remove, set, remove.</summary>
    private sealed class FakeStore
    {
        public string? Primary { get; set; }

        public Dictionary<string, Dictionary<string, List<string>>> Keys { get; } = new(StringComparer.Ordinal);

        public List<string> Scripts { get; } = [];

        public string Run(string script)
        {
            Scripts.Add(script);
            var current = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var output = new List<string>();
            foreach (var line in script.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "show" when parts[1] == "State:/Network/Global/IPv4":
                        output.Add(Primary is null ? "  No such key" : $"<dictionary> {{\n  PrimaryService : {Primary}\n}}");
                        break;
                    case "show":
                        output.Add(Keys.TryGetValue(parts[1], out var shown) ? Show(shown) : "  No such key");
                        break;
                    case "d.init":
                        current = new(StringComparer.Ordinal);
                        break;
                    case "get":
                        current = Keys.TryGetValue(parts[1], out var got) ? got.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal) : current;
                        break;
                    case "d.add":
                        current[parts[1]] = [.. parts.Skip(3)];
                        break;
                    case "d.remove":
                        current.Remove(parts[1]);
                        break;
                    case "set":
                        Keys[parts[1]] = current;
                        break;
                    case "remove":
                        Keys.Remove(parts[1]);
                        break;
                }
            }

            return string.Join('\n', output) + "\n";
        }

        private static string Show(Dictionary<string, List<string>> entry) =>
            "<dictionary> {\n" + string.Concat(entry.Select(p => $"  {p.Key} : <array> {{\n"
                + string.Concat(p.Value.Select((v, i) => $"    {i} : {v}\n")) + "  }\n")) + "}";
    }
}
