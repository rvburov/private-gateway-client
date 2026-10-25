using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Системный прокси пользователя: разбор настроек Windows, GNOME, KDE и macOS, свой ли он (вход нашего ядра в
/// режиме прокси) и строка «Другие VPN и прокси» в проверке.
/// </summary>
public sealed class UserProxyTests
{
    [Fact]
    public void Windows_settings_give_the_script_first_then_the_http_proxy()
    {
        UserProxySettings.FromWindows(1, "127.0.0.1:7890", null).ShouldBe(new UserProxy("127.0.0.1:7890"));
        UserProxySettings.FromWindows(1, "ftp=192.0.2.1:21;http=192.0.2.1:3128;https=192.0.2.1:3129", null).ShouldBe(new UserProxy("192.0.2.1:3128"),
            "по протоколам — берется http");
        UserProxySettings.FromWindows(1, "socks=192.0.2.1:1080", null).ShouldBe(new UserProxy("192.0.2.1:1080"), "без http — первый");
        UserProxySettings.FromWindows(0, "127.0.0.1:7890", null).ShouldBeNull("прокси выключен");
        UserProxySettings.FromWindows(1, "  ", null).ShouldBeNull();
        UserProxySettings.FromWindows(0, null, "http://example.com/proxy.pac").ShouldBe(new UserProxy("http://example.com/proxy.pac", IsScript: true));
        UserProxySettings.FromWindows(1, "127.0.0.1:7890", "http://example.com/proxy.pac").ShouldBe(new UserProxy("http://example.com/proxy.pac", IsScript: true),
            "включены оба — Windows берет скрипт автонастройки раньше ручного прокси");
    }

    [Fact]
    public void Gnome_settings_follow_the_mode()
    {
        var values = new Dictionary<string, string>
        {
            ["org.gnome.system.proxy mode"] = "'manual'\n",
            ["org.gnome.system.proxy.http host"] = "''",
            ["org.gnome.system.proxy.http port"] = "0",
            ["org.gnome.system.proxy.https host"] = "'192.0.2.7'",
            ["org.gnome.system.proxy.https port"] = "8443",
            ["org.gnome.system.proxy.socks host"] = "'127.0.0.1'",
            ["org.gnome.system.proxy.socks port"] = "1080",
            ["org.gnome.system.proxy autoconfig-url"] = "''",
        };
        string? Get(string schema, string key) => values.GetValueOrDefault(schema + " " + key);

        UserProxySettings.FromGnome(Get).ShouldBe(new UserProxy("192.0.2.7:8443"), "http пустой — https");

        values["org.gnome.system.proxy mode"] = "'auto'";
        UserProxySettings.FromGnome(Get).ShouldBe(new UserProxy("WPAD", IsScript: true), "автонастройка без адреса скрипта — WPAD");

        values["org.gnome.system.proxy mode"] = "'none'";
        UserProxySettings.FromGnome(Get).ShouldBeNull();
        UserProxySettings.FromGnome((_, _) => null).ShouldBeNull("gsettings нет — не GNOME");
    }

    [Fact]
    public void Kde_settings_follow_the_proxy_type()
    {
        const string Manual = """
            [Proxy Settings]
            NoProxyFor=localhost,127.0.0.1
            ProxyType=1
            httpProxy=http://192.0.2.9 3128
            socksProxy=socks://127.0.0.1 1080

            [Other]
            ProxyType=2
            """;

        UserProxySettings.FromKde(Manual).ShouldBe(new UserProxy("192.0.2.9:3128"));
        UserProxySettings.FromKde("[Proxy Settings]\nProxyType=1\nhttpProxy=\nhttpsProxy[$e]=https://192.0.2.9:8443/\n")
            .ShouldBe(new UserProxy("192.0.2.9:8443"), "адрес бывает и с двоеточием, ключ — с пометкой KDE");
        UserProxySettings.FromKde("[Proxy Settings]\nProxyType=2\nProxy Config Script=http://example.com/proxy.pac\n")
            .ShouldBe(new UserProxy("http://example.com/proxy.pac", IsScript: true));
        UserProxySettings.FromKde("[Proxy Settings]\nProxyType=3\n").ShouldBe(new UserProxy("WPAD", IsScript: true));
        UserProxySettings.FromKde("[Proxy Settings]\nProxyType=0\nhttpProxy=http://192.0.2.9 3128\n").ShouldBeNull("прокси выключен");
        UserProxySettings.FromKde(null).ShouldBeNull("файла нет");
    }

    [Fact]
    public void Macos_settings_give_the_script_first_then_web_then_socks()
    {
        const string Manual = """
            <dictionary> {
              ExceptionsList : <array> {
                0 : *.local
                1 : 169.254/16
              }
              FTPPassive : 1
              HTTPEnable : 0
              HTTPSEnable : 1
              HTTPSPort : 7890
              HTTPSProxy : 127.0.0.1
              SOCKSEnable : 1
              SOCKSPort : 7891
              SOCKSProxy : 127.0.0.1
            }
            """;

        UserProxySettings.FromScutil(Manual).ShouldBe(new UserProxy("127.0.0.1:7890"), "http выключен — https");
        UserProxySettings.FromScutil(Manual.Replace("FTPPassive : 1", "ProxyAutoConfigEnable : 1\n  ProxyAutoConfigURLString : http://example.com/p.pac", StringComparison.Ordinal))
            .ShouldBe(new UserProxy("http://example.com/p.pac", IsScript: true));
        UserProxySettings.FromScutil("<dictionary> {\n  HTTPEnable : 0\n}\n").ShouldBeNull();
        UserProxySettings.FromScutil(null).ShouldBeNull("scutil не ответил");
    }

    [Fact]
    public void Proxy_is_ours_only_on_our_port_in_proxy_mode_with_the_system_proxy_on()
    {
        var proxyMode = AppSettings.Default with { Mode = ConnectionMode.Proxy, Connection = ConnectionSettings.Default with { ProxyPort = 10808, SystemProxy = true } };
        var ours = new UserProxy("127.0.0.1:10808");

        ours.IsForeign(proxyMode).ShouldBeFalse("вход нашего ядра — его ставит служба пользователя");
        new UserProxy("127.0.0.1:7890").IsForeign(proxyMode).ShouldBeTrue("другой порт — другая программа");
        ours.IsForeign(proxyMode with { Mode = ConnectionMode.Vpn }).ShouldBeTrue("в режиме VPN системный прокси служба не ставит");
        ours.IsForeign(proxyMode with { Connection = proxyMode.Connection with { SystemProxy = false } }).ShouldBeTrue("свой системный прокси выключен");
        new UserProxy("127.0.0.1:10808", IsScript: true).IsForeign(proxyMode).ShouldBeTrue("скриптов служба не ставит");
    }

    [Fact]
    public void Foreign_proxy_turns_other_vpn_into_a_warning_unless_the_service_found_a_vpn()
    {
        var at = DateTimeOffset.UnixEpoch;
        var clean = new DiagnosticReport([new(DiagnosticCheck.Service, CheckState.Ok), new(DiagnosticCheck.OtherVpn, CheckState.Ok)], at);
        var vpn = AppSettings.Default with { Mode = ConnectionMode.Vpn };
        var foreign = new UserProxy("127.0.0.1:7890");

        UserProxy.WithUserProxy(clean, foreign, vpn)[DiagnosticCheck.OtherVpn].ShouldBe(new DiagnosticResult(DiagnosticCheck.OtherVpn, CheckState.Warning, "system_proxy"));
        UserProxy.WithUserProxy(clean, foreign, vpn)[DiagnosticCheck.Service]!.State.ShouldBe(CheckState.Ok, "остальное — как у службы");
        UserProxy.WithUserProxy(clean, null, vpn).ShouldBeSameAs(clean, "прокси нет — отчет службы как есть");

        var proxyMode = vpn with { Mode = ConnectionMode.Proxy };
        UserProxy.WithUserProxy(clean, new UserProxy("127.0.0.1:" + proxyMode.Connection.ProxyPort), proxyMode).ShouldBeSameAs(clean, "свой прокси — не чужой");

        var adapter = new DiagnosticReport([new(DiagnosticCheck.OtherVpn, CheckState.Warning, "vpn_adapter")], at);
        UserProxy.WithUserProxy(adapter, foreign, vpn).ShouldBeSameAs(adapter, "чужой VPN, найденный службой, важнее");

        var noService = new DiagnosticReport([new(DiagnosticCheck.Service, CheckState.Stopped, "not_found")], at);
        UserProxy.WithUserProxy(noService, foreign, vpn)[DiagnosticCheck.OtherVpn]!.Reason.ShouldBe("system_proxy", "прокси окно видит и без службы");
    }
}
