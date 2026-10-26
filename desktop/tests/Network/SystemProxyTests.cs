using Microsoft.Win32;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Системный прокси в режиме прокси: настройки ОС (GNOME — на подделке gsettings, KDE — во временном
/// kioslaverc, Windows — в своем разделе реестра, а не в настоящем), снимок прежних настроек и его возврат, связка со службой.
/// Настоящие настройки машины тесты не трогают.
/// </summary>
public sealed class SystemProxyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-proxy-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ServiceJournal _journal = new(TimeProvider.System);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Backup => Path.Combine(_root, "system-proxy.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Gnome_proxy_points_every_protocol_at_the_core_and_comes_back()
    {
        var gsettings = new FakeGsettings(new()
        {
            ["org.gnome.system.proxy mode"] = "'auto'",
            ["org.gnome.system.proxy ignore-hosts"] = "['localhost']",
            ["org.gnome.system.proxy.http host"] = "'proxy.example.com'",
            ["org.gnome.system.proxy.http port"] = "8080",
            ["org.gnome.system.proxy.https host"] = "''",
            ["org.gnome.system.proxy.https port"] = "0",
            ["org.gnome.system.proxy.socks host"] = "''",
            ["org.gnome.system.proxy.socks port"] = "0",
        });
        var proxy = new GnomeSystemProxy(gsettings.Run);
        var before = proxy.Read();

        proxy.Enable(10808);

        gsettings.Values.Where(v => v.Key.EndsWith(" host", StringComparison.Ordinal)).Select(v => v.Value).ShouldAllBe(v => v == "'127.0.0.1'");
        gsettings.Values.Where(v => v.Key.EndsWith(" port", StringComparison.Ordinal)).Select(v => v.Value).ShouldAllBe(v => v == "10808");
        gsettings.Values["org.gnome.system.proxy ignore-hosts"].ShouldBe(GnomeSystemProxy.Bypass);
        gsettings.Values["org.gnome.system.proxy mode"].ShouldBe("'manual'");
        gsettings.Written[^1].ShouldBe("org.gnome.system.proxy mode", "режим — последним: адреса уже на месте");

        proxy.Restore(before);

        gsettings.Values.ShouldBe(gsettings.Initial, ignoreOrder: true);
        gsettings.Written[^1].ShouldBe("org.gnome.system.proxy mode");
        proxy.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void Kde_proxy_points_every_protocol_at_the_core_and_comes_back_leaving_the_rest_of_the_file()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "kioslaverc");
        const string Before = """
            [$Version]
            update_info=kioslave.upd:esr

            [Proxy Settings]
            NoProxyFor=localhost
            ProxyType=2
            Proxy Config Script=http://example.com/proxy.pac
            httpProxy[$e]=http://proxy.example.com 8080

            [UserAgent]
            DefaultUserAgent=Mozilla/5.0
            """;
        File.WriteAllText(file, Before.ReplaceLineEndings("\n") + "\n");
        var reparsed = 0;
        var proxy = new KdeSystemProxy(file, () => reparsed++);
        var before = proxy.Read();

        proxy.Enable(10808);

        var text = File.ReadAllText(file);
        text.ShouldContain("[Proxy Settings]\nProxy Config Script=http://example.com/proxy.pac\n" +
            "httpProxy=http://127.0.0.1 10808\nhttpsProxy=http://127.0.0.1 10808\nsocksProxy=socks://127.0.0.1 10808\n" +
            $"NoProxyFor={KdeSystemProxy.Bypass}\nProxyType=1\n\n[UserAgent]", Case.Sensitive, "тип — последним: адреса уже на месте");
        text.ShouldStartWith("[$Version]\nupdate_info=kioslave.upd:esr\n\n", Case.Sensitive, "чужие разделы — как были");
        text.ShouldEndWith("[UserAgent]\nDefaultUserAgent=Mozilla/5.0\n");
        reparsed.ShouldBe(1, "KDE просят перечитать настройки");

        proxy.Restore(before);

        File.ReadAllText(file).ShouldBe(("""
            [$Version]
            update_info=kioslave.upd:esr

            [Proxy Settings]
            Proxy Config Script=http://example.com/proxy.pac
            httpProxy[$e]=http://proxy.example.com 8080
            NoProxyFor=localhost
            ProxyType=2

            [UserAgent]
            DefaultUserAgent=Mozilla/5.0
            """ + "\n").ReplaceLineEndings("\n"), "прежние значения вернулись с пометками KDE; httpsProxy и socksProxy не было — их и нет");
        File.Exists(file + ".pg-tmp").ShouldBeFalse();
    }

    [Fact]
    public void Kde_proxy_without_the_file_creates_the_section_and_takes_it_back_empty()
    {
        var file = Path.Combine(_root, "config", "kioslaverc");
        var proxy = new KdeSystemProxy(file, null);
        var before = proxy.Read();

        before.Values.ShouldBeEmpty("файла нет — ни одного ключа");

        proxy.Enable(10808);

        new KdeSystemProxy(file, null).Read().Values["ProxyType"].ShouldBe("1");

        proxy.Restore(before);

        File.ReadAllText(file).ShouldBe("[Proxy Settings]\n", "прокси выключен, как и было: ключей нет");

        File.WriteAllText(file, "[UserAgent]\nDefaultUserAgent=Mozilla/5.0\n");
        proxy.Enable(10808);

        File.ReadAllText(file).ShouldStartWith("[UserAgent]\nDefaultUserAgent=Mozilla/5.0\n\n[Proxy Settings]\nhttpProxy=", Case.Sensitive,
            "раздела не было — он дописан после чужих, через пустую строку");
    }

    [Fact]
    public void Without_gsettings_there_is_no_gnome_proxy()
    {
        var proxy = new GnomeSystemProxy(_ => null);

        proxy.IsAvailable.ShouldBeFalse();
        Should.Throw<IOException>(() => proxy.Enable(10808), "не записалось — это ошибка, а не тишина");
    }

    [Fact]
    public void Windows_proxy_goes_to_the_core_without_the_autoconfig_script_and_comes_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("параметры Интернета есть только на Windows");
            return;
        }

        var path = @"Software\PrivateGateway.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(path))
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("AutoConfigURL", "http://example.com/proxy.pac");
            }

            var proxy = new WindowsSystemProxy(path, notify: false);
            var before = proxy.Read();

            proxy.Enable(10808);

            using (var key = Registry.CurrentUser.OpenSubKey(path)!)
            {
                (key.GetValue("ProxyEnable"), key.GetValue("ProxyServer"), key.GetValue("ProxyOverride"), key.GetValue("AutoConfigURL"))
                    .ShouldBe((1, "127.0.0.1:10808", WindowsSystemProxy.Bypass, null));
            }

            proxy.Restore(before);

            using (var key = Registry.CurrentUser.OpenSubKey(path)!)
            {
                (key.GetValue("ProxyEnable"), key.GetValue("ProxyServer"), key.GetValue("ProxyOverride"), key.GetValue("AutoConfigURL"))
                    .ShouldBe((0, null, null, "http://example.com/proxy.pac"), "все как было — и чего не было, того нет");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Guard_keeps_the_settings_from_before_the_first_switch_on()
    {
        var proxy = new FakeSystemProxy();
        var guard = new SystemProxyGuard(proxy, Backup, _journal);

        guard.Apply(10808);
        guard.Apply(2080);

        (proxy.Port, guard.IsActive).ShouldBe((2080, true));
        File.Exists(Backup).ShouldBeTrue("снимок — в файле: переживет сбой службы");

        guard.Release();

        proxy.Port.ShouldBeNull("вернулось то, что было до первого включения, а не порт 10808");
        (File.Exists(Backup), guard.IsActive).ShouldBe((false, false));
        _journal.Tail(10).Select(e => e.Message).ShouldBe(
        [
            "Системный прокси включен (подделка): 127.0.0.1:10808",
            "Системный прокси включен (подделка): 127.0.0.1:2080",
            "Системный прокси возвращен, как был",
        ]);
    }

    [Fact]
    public void Guard_restores_settings_left_after_a_crash()
    {
        var proxy = new FakeSystemProxy();
        new SystemProxyGuard(proxy, Backup, _journal).Apply(10808);

        // Служба упала: снимок остался, прокси включен. Новая служба возвращает его при запуске.
        var restarted = new SystemProxyGuard(proxy, Backup, _journal);

        restarted.RestoreStale().ShouldBeTrue();
        (proxy.Port, File.Exists(Backup)).ShouldBe((null, false));
        restarted.RestoreStale().ShouldBeFalse("возвращать больше нечего");
    }

    [Fact]
    public void Stale_snapshot_is_not_overwritten_by_settings_of_the_crashed_run()
    {
        var proxy = new FakeSystemProxy();
        new SystemProxyGuard(proxy, Backup, _journal).Apply(10808);
        var restarted = new SystemProxyGuard(proxy, Backup, _journal);

        restarted.Apply(10808);
        restarted.Release();

        proxy.Port.ShouldBeNull("в снимке — настройки до программы, а не ее же прокси");
    }

    [Fact]
    public void Settings_that_cannot_be_changed_do_not_break_the_connection()
    {
        var guard = new SystemProxyGuard(new FakeSystemProxy { Refuses = true }, Backup, _journal);

        guard.Apply(10808);

        guard.IsActive.ShouldBeFalse();
        _journal.Tail(1).ShouldHaveSingleItem().Message.ShouldStartWith("Системный прокси (подделка) не изменен");
    }

    [Fact]
    public async Task Proxy_mode_switches_the_system_proxy_on_while_connected()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var proxy = new FakeSystemProxy();
        await using var service = await RunningService.StartAsync(systemProxy: (proxy, Backup));
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);

        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, proxyPort, systemProxy: true), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        proxy.Port.ShouldBe(proxyPort);

        await service.Client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);

        proxy.Port.ShouldBeNull("отключилась защита — вернулись прежние настройки");
        File.Exists(Backup).ShouldBeFalse();
    }

    [Fact]
    public async Task Refused_switch_and_the_setting_off_leave_the_system_proxy_alone()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var proxy = new FakeSystemProxy();
        await using var service = await RunningService.StartAsync(systemProxy: (proxy, Backup));
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var proxyPort = XrayKit.FreePort();
        using var watch = service.Client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, proxyPort, systemProxy: true), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        var hysteria = ServiceIpcTests.Request(XrayKit.Definition(ProxyProtocol.Hysteria2, 443, new ServerCredentials(Password: XrayKit.Password))
            with
        { Transport = TransportOptions.Quic }, proxyPort, systemProxy: true);
        await service.Client.ConnectAsync(hysteria, cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Error);

        proxy.Port.ShouldBeNull("подключения нет и прежнее остановлено — программы идут напрямую, так и сказано");

        await service.Client.ConnectAsync(ServiceIpcTests.Request(definition, proxyPort), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Connected);

        proxy.Port.ShouldBeNull("настройка выключена — системный прокси не трогается");
        File.Exists(Backup).ShouldBeFalse();
    }

    /// <summary>gsettings в памяти: get — значение ключа, set — запись; порядок записей запоминается.</summary>
    private sealed class FakeGsettings(Dictionary<string, string> initial)
    {
        public Dictionary<string, string> Initial { get; } = new(initial);

        public Dictionary<string, string> Values { get; } = new(initial);

        public List<string> Written { get; } = [];

        public string? Run(IReadOnlyList<string> arguments)
        {
            var key = arguments[1] + " " + arguments[2];
            switch (arguments[0])
            {
                case "get":
                    return Values.TryGetValue(key, out var value) ? value + "\n" : null;
                case "set":
                    Values[key] = arguments[3];
                    Written.Add(key);
                    return string.Empty;
                default:
                    return null;
            }
        }
    }
}

/// <summary>Настройки прокси «ОС» в памяти: включен — порт, выключен — null.</summary>
internal sealed class FakeSystemProxy : ISystemProxy
{
    public int? Port { get; private set; }

    public bool Refuses { get; init; }

    public string Name => "подделка";

    public SystemProxySnapshot Read() => new(new Dictionary<string, string?> { ["port"] = Port?.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    public void Enable(int port) => Port = Refuses ? throw new UnauthorizedAccessException("нет доступа") : port;

    public void Restore(SystemProxySnapshot previous) =>
        Port = previous.Values["port"] is { } port ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : null;
}
