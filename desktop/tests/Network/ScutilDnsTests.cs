using System.Diagnostics;
using System.Net;
using System.Text.Json;
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

    private const string OwnKey = "State:/Network/Service/PrivateGateway-Tunnel/DNS";

    [Fact]
    public void Supplemental_dns_uses_the_tunnel_without_binding_to_the_physical_network()
    {
        _store.Keys["State:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["192.168.1.1"], ["SearchDomains"] = ["example.org"] };
        _store.Keys["Setup:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["192.0.2.53"] };
        var dns = new ScutilDns(_store.Run, Backup);

        dns.Apply(Tunnel);
        dns.Apply(Tunnel);

        _store.Keys[OwnKey]["ServerAddresses"].ShouldBe(["172.19.0.2"]);
        _store.Keys[OwnKey]["SupplementalMatchDomains"].ShouldBe(["\"\""]);
        _store.Scripts.ShouldContain(s => s.Contains("d.add SupplementalMatchOrders * # 1", StringComparison.Ordinal));
        _store.Keys[OwnKey].ShouldNotContainKey("InterfaceName", "адрес туннеля нельзя привязать к Wi-Fi");
        _store.Keys["State:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.168.1.1"]);
        _store.Keys["Setup:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.0.2.53"]);
        _store.Keys["State:/Network/Service/WIFI/DNS"]["SearchDomains"].ShouldBe(["example.org"]);
        File.Exists(Backup).ShouldBeFalse("DNS физической сети больше не меняется");

        // DHCP сменил DNS во время VPN; новый экземпляр службы убирает только свою запись.
        _store.Keys["State:/Network/Service/WIFI/DNS"]["ServerAddresses"] = ["192.168.1.2"];
        new ScutilDns(_store.Run, Backup).Restore();
        dns.Restore();
        _store.Keys.ShouldNotContainKey(OwnKey);
        _store.Keys["State:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.168.1.2"]);
    }

    [Fact]
    public void Upgrade_restores_legacy_physical_dns_before_registering_the_supplemental_resolver()
    {
        _store.Keys["State:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["172.19.0.2"], ["SearchDomains"] = ["example.org"] };
        _store.Keys["Setup:/Network/Service/WIFI/DNS"] = new() { ["ServerAddresses"] = ["172.19.0.2"] };
        File.WriteAllText(Backup, JsonSerializer.Serialize(new { Service = "WIFI", State = (string[])["192.168.1.1"], Setup = (string[])["192.0.2.53"] }));
        var dns = new ScutilDns(_store.Run, Backup);
        dns.Apply(Tunnel);

        _store.Keys["State:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.168.1.1"]);
        _store.Keys["Setup:/Network/Service/WIFI/DNS"]["ServerAddresses"].ShouldBe(["192.0.2.53"]);
        _store.Keys["State:/Network/Service/WIFI/DNS"]["SearchDomains"].ShouldBe(["example.org"]);
        _store.Keys[OwnKey]["ServerAddresses"].ShouldBe(["172.19.0.2"]);
        File.Exists(Backup).ShouldBeFalse();
    }

    [Fact]
    public void Strange_answers_of_the_os_do_not_reach_the_commands()
    {
        Should.Throw<IOException>(() => new ScutilDns(_store.Run, Backup, "bad; remove x").Apply(Tunnel));
        _store.Scripts.ShouldBeEmpty();
        File.WriteAllText(Backup, JsonSerializer.Serialize(new { Service = "WIFI", State = (string[])["192.168.1.1; remove x"], Setup = (string[]?)null }));
        Should.Throw<IOException>(() => new ScutilDns(_store.Run, Backup).Apply(Tunnel));
        _store.Scripts.ShouldBeEmpty("непонятный прежний адрес в команду не попадает");
        File.Exists(Backup).ShouldBeTrue("не терять копию при отказе восстановления");
    }

    [Fact]
    public void Scutil_output_is_read_as_scutil_prints_it()
    {
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
            // Копия старой версии: восстановление без регистрации резолвера машины.
            File.WriteAllText(Backup, JsonSerializer.Serialize(new { Service = service, State = (string[])["192.0.2.54"], Setup = (string[]?)null }));
            new ScutilDns(Scutil, Backup, service + "-own").Restore();
            var restored = Scutil($"show {key}\nquit\n");
            ScutilDns.ParseServers(restored).ShouldBe(["192.0.2.54"]);
            restored.ShouldContain("example.org");
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
