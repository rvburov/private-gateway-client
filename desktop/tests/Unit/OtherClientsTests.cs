using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Другие программы VPN и прокси (<see cref="OtherClients"/>): помеха — их работающее ядро, туннель и слушающий вход системного
/// прокси; окно программы без ядра — не помеха; свои ядра — не в счет; название — по окну, папке, адаптеру или владельцу входа.
/// </summary>
public sealed class OtherClientsTests
{
    private const string OwnXray = @"C:\Program Files\Private Gateway\cores\xray.exe";

    private static readonly string[] Own = [OwnXray, @"C:\Program Files\Private Gateway\cores\sing-box.exe"];

    [Fact]
    public void A_running_core_is_named_after_the_app_in_its_folder()
    {
        RunningProcess[] processes =
        [
            new(10, "v2rayN", @"C:\Tools\v2rayN\v2rayN.exe"),
            new(11, "xray", @"C:\Tools\v2rayN\bin\xray\xray.exe"),
            new(12, "Hiddify", @"C:\Program Files\Hiddify\Hiddify.exe"),
            new(13, "xray", OwnXray),
            new(14, "explorer", null),
        ];

        OtherClients.Find(processes, Own, []).ShouldBe([new OtherClient("v2rayN", OtherClientSign.Process, "xray")],
            "ядро v2rayN — из его папки; Hiddify открыт без ядра — не помеха; свое ядро службы — не в счет");
    }

    [Fact]
    public void A_core_without_its_app_folder_takes_the_only_open_app_or_its_own_name()
    {
        OtherClients.Find([new(1, "HiddifyCli", null)], Own, []).ShouldBe([new OtherClient("Hiddify", OtherClientSign.Process, "HiddifyCli")]);
        OtherClients.Find([new(1, "mihomo", "/usr/local/bin/mihomo"), new(2, "clash-verge", "/opt/Clash Verge/clash-verge")], Own, [])
            .ShouldBe([new OtherClient("Clash Verge", OtherClientSign.Process, "mihomo")], "окно одно — ядро его");
        OtherClients.Find([new(1, "sing-box", "/opt/other/sing-box")], Own, []).ShouldBe([new OtherClient("sing-box", OtherClientSign.Process, "sing-box")],
            "sing-box сам и окно, и ядро — чужой по пути");
        OtherClients.Find([new(1, "XRAY", null)], Own, []).ShouldBe([new OtherClient("Xray", OtherClientSign.Process, "XRAY")],
            "путь не прочитан — своим не признать; имя — без учета регистра");
        OtherClients.Find([new(1, "xray", OwnXray.ToUpperInvariant())], Own, []).ShouldBe(OperatingSystem.IsLinux() ? [new OtherClient("Xray", OtherClientSign.Process, "xray")] : [],
            "пути Windows и macOS — без учета регистра");
    }

    [Fact]
    public void Apps_alone_and_unknown_processes_are_not_a_hindrance()
    {
        OtherClients.Find([new(1, "Hiddify", null), new(2, "v2rayN", null), new(3, "chrome", null), new(4, "wireguard", null)], Own, []).ShouldBeEmpty();
        OtherClients.IsKnown("HiddifyCli").ShouldBeTrue();
        OtherClients.IsKnown("chrome").ShouldBeFalse();
    }

    [Fact]
    public void A_tunnel_is_named_by_its_adapter_or_the_only_open_app()
    {
        OtherClients.Find([], Own, ["Hiddify Tunnel", "wg0", "outline-tap0"]).ShouldBe(
        [
            new OtherClient("Hiddify", OtherClientSign.Adapter, "Hiddify Tunnel"),
            new OtherClient(string.Empty, OtherClientSign.Adapter, "wg0"),
            new OtherClient("Outline", OtherClientSign.Adapter, "outline-tap0"),
        ]);
        OtherClients.Find([new(1, "NekoBox", null)], Own, ["tun0"]).ShouldBe([new OtherClient("NekoBox", OtherClientSign.Adapter, "tun0")]);
    }

    [Fact]
    public void A_listening_system_proxy_is_named_by_the_owner_of_its_entry()
    {
        RunningProcess[] processes = [new(20, "Hiddify", @"C:\Hiddify\Hiddify.exe"), new(21, "v2rayN", @"C:\v2rayN\v2rayN.exe"), new(22, "privoxy", null)];

        OtherClients.Find(processes, Own, [], ("127.0.0.1:2334", 20)).ShouldBe([new OtherClient("Hiddify", OtherClientSign.SystemProxy, "127.0.0.1:2334")],
            "ядро Hiddify — внутри окна: его выдает вход прокси");
        OtherClients.Find(processes, Own, [], ("127.0.0.1:8118", 22)).ShouldBe([new OtherClient("privoxy", OtherClientSign.SystemProxy, "127.0.0.1:8118")],
            "незнакомая программа — по имени процесса");
        OtherClients.Find(processes, Own, [], ("127.0.0.1:2334", null)).ShouldBe([new OtherClient(string.Empty, OtherClientSign.SystemProxy, "127.0.0.1:2334")],
            "владелец неизвестен, окон два — без названия");
    }

    [Theory]
    [InlineData("127.0.0.1:2334", 2334)]
    [InlineData("localhost:7890", 7890)]
    [InlineData("[::1]:1080", 1080)]
    [InlineData("192.0.2.10:3128", null)]
    [InlineData("proxy.example.com:8080", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("127.0.0.1:70000", null)]
    public void Only_a_proxy_on_this_machine_has_a_local_port(string address, int? port) => OtherClients.LocalPort(address).ShouldBe(port);

    [Fact]
    public void The_list_compares_by_content()
    {
        var one = new OtherClientList([new OtherClient("Hiddify", OtherClientSign.Process, "HiddifyCli")]);
        var same = new OtherClientList([new OtherClient("Hiddify", OtherClientSign.Process, "HiddifyCli")]);

        one.ShouldBe(same);
        one.GetHashCode().ShouldBe(same.GetHashCode());
        one.ShouldNotBe(new OtherClientList([new OtherClient("Hiddify", OtherClientSign.SystemProxy, "127.0.0.1:2334")]));
        new OtherClientList([new("Hiddify", OtherClientSign.Process, "a"), new("Hiddify", OtherClientSign.SystemProxy, "b"), new(string.Empty, OtherClientSign.Adapter, "wg0")])
            .Programs("другая").ShouldBe(["Hiddify", "другая"]);
    }
}
