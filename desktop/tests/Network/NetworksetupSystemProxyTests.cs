using PrivateGateway.Client.Platform.MacOS;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Системный прокси macOS (<see cref="NetworksetupSystemProxy"/>): с подделкой networksetup — что включается у каких сетей
/// и как возвращается чужое; и настоящий networksetup на macOS от root — снимок и возврат его же: команды проходят, а
/// настройки машины CI остаются как были (прокси на ней не включается — CI потерял бы связь).
/// </summary>
public sealed class NetworksetupSystemProxyTests
{
    [Fact]
    public void Every_enabled_network_goes_to_the_core_and_gets_back_its_own_proxy_script_and_exceptions()
    {
        var tool = new FakeNetworksetup();
        tool.Services["Wi-Fi"] = new() { ["web"] = ("Yes", "proxy.example.com", "3128"), ["auto"] = ("Yes", "http://wpad.example.com/proxy.pac", "") };
        tool.Bypass["Wi-Fi"] = ["*.local", "169.254/16"];
        tool.Services["USB 10/100/1000 LAN"] = new() { ["secureweb"] = ("No", "proxy.example.org", "8443") };
        tool.Services["Thunderbolt Bridge"] = new();
        tool.Disabled.Add("Thunderbolt Bridge");
        var proxy = new NetworksetupSystemProxy(tool.Run);
        var before = proxy.Read();

        proxy.Enable(10808);

        foreach (var service in new[] { "Wi-Fi", "USB 10/100/1000 LAN" })
        {
            foreach (var kind in new[] { "web", "secureweb", "socksfirewall" })
            {
                tool.Services[service][kind].ShouldBe(("Yes", "127.0.0.1", "10808"), $"{service}: {kind}");
            }

            tool.Get(service, "auto").Enabled.ShouldBe("No", "скрипт автонастройки браузер взял бы раньше ручного прокси");
            tool.Bypass[service].ShouldBe(NetworksetupSystemProxy.Bypass);
        }

        tool.Services["Thunderbolt Bridge"].ShouldBeEmpty("выключенная сеть не трогается");

        proxy.Restore(before);

        tool.Get("Wi-Fi", "web").ShouldBe(("Yes", "proxy.example.com", "3128"), "чужой прокси — как был, включен");
        tool.Get("Wi-Fi", "auto").ShouldBe(("Yes", "http://wpad.example.com/proxy.pac", ""), "скрипт автонастройки — снова включен");
        tool.Get("Wi-Fi", "secureweb").Enabled.ShouldBe("No");
        tool.Get("Wi-Fi", "socksfirewall").Enabled.ShouldBe("No");
        tool.Bypass["Wi-Fi"].ShouldBe(["*.local", "169.254/16"]);
        tool.Get("USB 10/100/1000 LAN", "secureweb").ShouldBe(("No", "proxy.example.org", "8443"), "выключенный с адресом — адрес на месте, выключен");
        tool.Get("USB 10/100/1000 LAN", "web").Enabled.ShouldBe("No");
        tool.Get("USB 10/100/1000 LAN", "auto").Enabled.ShouldBe("No");
        tool.Bypass["USB 10/100/1000 LAN"].ShouldBeEmpty("исключений не было — и нет");
    }

    [Fact]
    public void Network_removed_while_connected_is_skipped_on_return()
    {
        var tool = new FakeNetworksetup();
        tool.Services["Wi-Fi"] = new();
        tool.Services["iPhone USB"] = new();
        var proxy = new NetworksetupSystemProxy(tool.Run);
        var before = proxy.Read();
        proxy.Enable(10808);
        tool.Services.Remove("iPhone USB");
        tool.Calls.Clear();

        proxy.Restore(before);

        tool.Get("Wi-Fi", "web").Enabled.ShouldBe("No");
        tool.Calls.ShouldNotContain(c => c.Contains("iPhone USB", StringComparison.Ordinal), "сети больше нет — возвращать некуда");
    }

    [Fact]
    public void Output_of_networksetup_is_read_as_macos_prints_it()
    {
        NetworksetupSystemProxy.ParseServices("An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Thunderbolt Bridge\nUSB 10/100/1000 LAN\n", enabledOnly: true)
            .ShouldBe(["Wi-Fi", "USB 10/100/1000 LAN"]);
        NetworksetupSystemProxy.ParseServices("An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Thunderbolt Bridge\n", enabledOnly: false)
            .ShouldBe(["Wi-Fi", "Thunderbolt Bridge"]);
        NetworksetupSystemProxy.ParseProxy("Enabled: Yes\nServer: 127.0.0.1\nPort: 10808\nAuthenticated Proxy Enabled: 0\n").ShouldBe((true, "127.0.0.1", 10808));
        NetworksetupSystemProxy.ParseProxy("Enabled: No\nServer: \nPort: 0\nAuthenticated Proxy Enabled: 0\n").ShouldBe((false, "", 0));
        NetworksetupSystemProxy.ParseProxy("").ShouldBe((false, "", 0), "непонятный вывод — прокси не включен");
        NetworksetupSystemProxy.ParseBypass("*.local\n169.254/16\n").ShouldBe(["*.local", "169.254/16"]);
        NetworksetupSystemProxy.ParseBypass("There aren't any bypass domains set on Wi-Fi.\n").ShouldBeEmpty();
    }

    [Fact]
    public void Real_networksetup_reads_and_puts_back_the_same_settings()
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("networksetup — только на macOS от root (CI)");
            return;
        }

        var proxy = NetworksetupSystemProxy.ForThisMachine().ShouldNotBeNull();
        var before = proxy.Read();
        before.Values.ShouldNotBeEmpty("у машины есть хотя бы одна включенная сеть");
        foreach (var (key, value) in before.Values.Where(v => v.Key.EndsWith("\nweb", StringComparison.Ordinal)))
        {
            value.ShouldNotBeNull().ShouldContain("Enabled:", customMessage: key);
        }

        proxy.Restore(before);

        proxy.Read().Values.ShouldBe(before.Values, ignoreOrder: true, "возврат снимка ничего не меняет");
    }

    /// <summary>Подделка networksetup: сети, их прокси и исключения — как их хранит и печатает macOS.</summary>
    private sealed class FakeNetworksetup
    {
        public Dictionary<string, Dictionary<string, (string Enabled, string Server, string Port)>> Services { get; } = [];

        public Dictionary<string, List<string>> Bypass { get; } = [];

        public HashSet<string> Disabled { get; } = [];

        public List<string> Calls { get; } = [];

        public (string Enabled, string Server, string Port) Get(string service, string kind) =>
            Services[service].GetValueOrDefault(kind, ("No", "", kind == "auto" ? "" : "0"));

        public string Run(IReadOnlyList<string> arguments)
        {
            Calls.Add(string.Join(' ', arguments));
            var command = arguments[0];
            if (command == "-listallnetworkservices")
            {
                return "An asterisk (*) denotes that a network service is disabled.\n"
                    + string.Concat(Services.Keys.Select(s => (Disabled.Contains(s) ? "*" : "") + s + "\n"));
            }

            var service = arguments[1];
            if (!Services.TryGetValue(service, out var proxies))
            {
                return $"** Error: The parameters were not valid.\n{service} is not a recognized network service.\n";
            }

            switch (command)
            {
                case "-getautoproxyurl":
                    var auto = Get(service, "auto");
                    return $"URL: {(auto.Server.Length > 0 ? auto.Server : "(null)")}\nEnabled: {auto.Enabled}\n";
                case "-setautoproxystate":
                    proxies["auto"] = Get(service, "auto") with { Enabled = arguments[2] == "on" ? "Yes" : "No" };
                    return string.Empty;
                case "-getproxybypassdomains":
                    return Bypass.GetValueOrDefault(service) is { Count: > 0 } list
                        ? string.Concat(list.Select(d => d + "\n"))
                        : $"There aren't any bypass domains set on {service}.\n";
                case "-setproxybypassdomains":
                    Bypass[service] = arguments[2] == "Empty" ? [] : [.. arguments.Skip(2)];
                    return string.Empty;
            }

            var kind = command[4..command.IndexOf("proxy", StringComparison.Ordinal)];
            if (command.StartsWith("-get", StringComparison.Ordinal))
            {
                var (enabled, server, port) = Get(service, kind);
                return $"Enabled: {enabled}\nServer: {server}\nPort: {port}\nAuthenticated Proxy Enabled: 0\n";
            }

            proxies[kind] = command.EndsWith("proxystate", StringComparison.Ordinal)
                ? Get(service, kind) with { Enabled = arguments[2] == "on" ? "Yes" : "No" }
                : ("Yes", arguments[2], arguments[3]);
            return string.Empty;
        }
    }
}
